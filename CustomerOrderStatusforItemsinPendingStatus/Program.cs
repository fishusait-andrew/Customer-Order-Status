using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;



internal static class Program
{
    // Confirmed shipping-related item IDs; retain lines without inventory commitment.
    private static readonly HashSet<long> ShippingItemIds = new()
    {
        165813, 165811, 165809, 165812, 165807, 165808, 1473396, 1572180, 318367, 1256377, 1256376, 81193, 524713, 81185, 81184
    };
    //SO Class
    //List of items
    //List of uncommited items
    //SO info
    private class SO
    {
        public long InternalId { get; set; }
        public string OrderNumber { get; set; } = string.Empty;

        public string CustomerOrderStatusId { get; set; } = string.Empty;

        public bool ReadyToFulfill { get; set; }
        public bool IsWholesale { get; set; }
        public bool FirstShipment { get; set; }

        public bool HasIncompleteItemData { get; set; }

        public List<OrderItem> Items { get; set; } = new();
        public List<OrderItem> UncommittedItems { get; set; } = new();

        public void RefreshUncommittedItems()
        {
            UncommittedItems.Clear();

            foreach (OrderItem item in Items)
            {
                if (item.IsClosed ||
                    !item.RequiresInventoryCommitment ||
                    item.RemainingQuantity == 0)
                {
                    continue;
                }

                if (item.UncommittedQuantity > 0)
                {
                    // Store the same object rather than creating a copy.
                    UncommittedItems.Add(item);
                }
            }
        }
    }

    private class OrderItem
    {
        public string LineId { get; set; } = string.Empty;
        public long ItemInternalId { get; set; }

        public string ItemName { get; set; } = string.Empty;
        public string ItemType { get; set; } = string.Empty;

        public long? LocationId { get; set; }
        public bool IsClosed { get; set; }

        // Set this explicitly when parsing the item type.
        public bool RequiresInventoryCommitment { get; set; }
        public decimal QuantityOrdered { get; set; }
        public decimal QuantityShipped { get; set; }
        public decimal QuantityCommitted { get; set; }

        public decimal RemainingQuantity =>
            Math.Max(0m, QuantityOrdered - QuantityShipped);

        public decimal UncommittedQuantity =>
            Math.Max(0m, RemainingQuantity - QuantityCommitted);

        public bool IsFullyCommitted => UncommittedQuantity == 0m;
    }

    private class ItemInventory
    {
        public long ItemInternalId { get; set; }

        public decimal TotalQuantityOnHand { get; set; }
        public decimal OnHoldInventory { get; set; }
        public decimal ProShopQuantityOnHand { get; set; }

        public List<InventoryBalance> Balances { get; set; } = new();

        // Set true only after all required inventory queries succeed.
        public bool RetrievalSucceeded { get; set; }
        public string? RetrievalError { get; set; }

        public decimal QuantityOnHandExcludingHold =>
            TotalQuantityOnHand - OnHoldInventory;

        public decimal GetStatusQuantity(long inventoryStatusId, HashSet<long> includedLocationIds)
        {
            if (!RetrievalSucceeded)
            {
                throw new InvalidOperationException($"Inventory retrieval is incomplete for item {ItemInternalId}.");
            }

            if (includedLocationIds.Count == 0)
            {
                throw new ArgumentException("At least one inventory location must be specified.", nameof(includedLocationIds));
            }

            decimal total = 0m;

            foreach (InventoryBalance balance in Balances)
            {
                if (balance.InventoryStatusId == inventoryStatusId && includedLocationIds.Contains(balance.LocationId))
                {
                    total += balance.QuantityOnHand;
                }
            }

            return total;
        }
    }

    private class InventoryBalance
    {
        public long LocationId { get; set; }
        public long InventoryStatusId { get; set; }
        public decimal QuantityOnHand { get; set; }
    }

    private class CustomFieldIds
    {
        public string CustomerOrderStatus { get; set; } = string.Empty;
        public string ReadyToFulfill { get; set; } = string.Empty;
        public string FirstShipment { get; set; } = string.Empty;
        public string WholesaleFlag { get; set; } = string.Empty;

        public string OnHoldInventory { get; set; } = "custitem_on_hold_inventory";
    }


    private class OrderUpdate
    {
        public long OrderInternalId { get; set; }
        public string OrderNumber { get; set; } = string.Empty;
        public string PreviousStatusId { get; set; } = string.Empty;
        public string NewStatusId { get; set; } = string.Empty;
        public bool? NewReadyToFulfill { get; set; }
        public string Reason { get; set; } = string.Empty;
        public List<long> RelevantItemIds { get; set; } = new();
    }


    //Item Class
    //Item Info

    //Stored Script Ids
    //Order Status Values
    //Ready to Fulfill
    //First Shipment
    //Wholesale flag
    //pro shop location id
    //pending and available inventory statuses
    //On hold inventory

    private static async Task Main(string[] args)
    {
        try
        {
            bool isDebug = false;
            Console.WriteLine($"INFO Event=RunStarted Utc={DateTimeOffset.UtcNow:O}");

            if (isDebug)
            {
                //Cloudrun will not use local files but this enables easy local testing.
                string envPath = "C:/Users/Andrew/Desktop/CloudRun Keys/On Hold Inventory/on-hold-inv.env";
                LoadEnvFile(envPath);
            }

            string accountId = Required("NETSUITE_ACCOUNT_ID");
            string clientId = Required("NETSUITE_CLIENT_ID");
            string certificateId = Required("NETSUITE_CERTIFICATE_ID");
            string privateKeyPath = Required("NETSUITE_PRIVATE_KEY");

            string accountDomain = accountId.Trim().ToLowerInvariant().Replace('_', '-');
            string baseUrl = $"https://{accountDomain}.suitetalk.api.netsuite.com";

            using var http = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(120)
            };

            await AuthenticateAsync(http, baseUrl, clientId, certificateId, privateKeyPath);

            var orders = new List<SO>();
            var ordersNeedingPendingTransfer = new List<SO>();
            var ordersById = new Dictionary<long, SO>();

            var itemIdsToRetrieve = new HashSet<long>();
            var itemInventories = new List<ItemInventory>();

            var proposedUpdates = new List<OrderUpdate>();

            //REST is set up here and can begin doing things with NetSuite.
            //Find all SOs but exclude those that have the following statuses:
            //Payment Hold, Incomplete Pick, External Fraud Review,
            //Angler Support - On Hold. Wholesale - On Hold

            //This should include both retail and wholesale orders
            //This should also include the orders item lines
            //The results should be stored in a list to begin

            //Once the list is populated it should be passed to a function that will parse through
            // the json and fill two classes. First the SO class, and a item class list inside of the SO class.

            //For each sales order, Identify each orders uncommitted remaining item lines
            //For each applicable item line:
            //1.Exclude closed lines and items already fully shipped.
            //2.Calculate remaining quantity = ordered quantity − shipped quantity.
            //3.Compare committed quantity with that remaining quantity.
            //4.If committed quantity is less than remaining quantity, include the line in the order’s uncommitted lines.
            //For example: an order line has quantity 5, with 2 shipped and 3 committed.Its remaining quantity is 3, so it is fully committed for this evaluation.
            //Ignore non - inventory lines that cannot be committed, such as descriptions or subtotals.
            //These uncommited items should be placed into the uncommited list for the SO
            Console.WriteLine("INFO Phase=OrderRetrieval Event=Started");
            List<JsonElement> orderRows = await GetElligibleOrders(http, baseUrl);
            PopulateOrders(orderRows, orders, ordersById, itemIdsToRetrieve);
            Console.WriteLine($"INFO Phase=OrderRetrieval Event=Completed Orders={orders.Count} UncommittedItemIds={itemIdsToRetrieve.Count}");

            //Remove shipping items from order item list. 
            //More ids can be added to the list above if we need to exclude certain things.
            await RemovingUnwantedItemLinesFromList(orders, ShippingItemIds);

            //Need to get each items inventory values
            long proShopLocationId = long.Parse(Required("NETSUITE_PRO_SHOP_LOCATION_ID"), System.Globalization.CultureInfo.InvariantCulture);

            //Getting more inventory details for each item on the SOs
            Console.WriteLine($"INFO Phase=InventoryRetrieval Event=Started ItemCount={itemIdsToRetrieve.Count}");
            itemInventories = await GetItemInventoryAsync(http, baseUrl, itemIdsToRetrieve, proShopLocationId);
            Console.WriteLine($"INFO Phase=InventoryRetrieval Event=Completed ItemCount={itemInventories.Count}");

            //If an item qualifies to be switched to Pending Cart Transfer, that is done here.
            Console.WriteLine("INFO Phase=PendingCartEntry Event=Started");
            await MoveOrdersToPendingCartTransfer(orders, itemInventories, http, baseUrl);
            Console.WriteLine("INFO Phase=PendingCartEntry Event=Completed");

            //For all Previous(Not Newly Created) pending cart transfer status order we need to check and see if they need moved into 
            // Backorder Pending Review status and have their ready to fulfill checkbox unchecked if not already. 
                //They qualify when
                //Must be a retail order
                //all of the lines and not fully committed, if they are noting happens
                //if any one item on the SO is both not fully committed and still has a quantity in Pending status, Do nothing
                //if all quantities of not fully committed lines are in available status, then make the changes.
            Console.WriteLine("INFO Phase=RetailPendingCartExit Event=Started");
            await MovePentingCartTransferOrdersToBackorderPendingReviewRetail(orders, itemInventories, http, baseUrl);
            Console.WriteLine("INFO Phase=RetailPendingCartExit Event=Completed");

            //For all Previous(Not Newly Created) pending cart transfer status wholesale orders we need to check for
            // first or second shipment and take actions based on which shipment it is.
            Console.WriteLine("INFO Phase=WholesalePendingCartExit Event=Started");
            await ChangeWsOrderStatusBasedOnItemCommitment(orders, itemInventories, http, baseUrl);
            Console.WriteLine("INFO Phase=WholesalePendingCartExit Event=Completed");

            Console.WriteLine($"INFO Event=RunCompleted Utc={DateTimeOffset.UtcNow:O}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR Event=RunFailed Utc={DateTimeOffset.UtcNow:O} ExceptionType={ex.GetType().Name} Message={ex.Message}");
            Environment.ExitCode = 1;
        }
    }

    private static async Task ChangeWsOrderStatusBasedOnItemCommitment(List<SO> orders, List<ItemInventory> itemInventoryData, HttpClient http, string baseUrl)
    {
        //Build WS List.
        // Build a list containing only:
        // 1. Wholesale orders
        // 2. Currently in Pending Cart Transfer
        // 3. With complete order and inventory data
        //If all items are fully committed, change the status to Wholesale - Approved Status and do nothing else
        //If there are uncommitted items, move to the first or second shipment decision path.
        var PendingCartTranferOrders = new List<SO>();
        string PendingCartTransferId = Required("NETSUITE_STATUS_CUSTOMER_ORDER");
        string WsApprovedStatusId = Required("NETSUITE_STATUS_WS_APPROVED");
        string WsBackorderStatusId = Required("NETSUITE_STATUS_WS_BACKORDER");

        //This will reduce our list to only orders that are in the Pending Cart Transfer status.
        foreach (var order in orders)
        {
            if (order.CustomerOrderStatusId == PendingCartTransferId)
            {
                PendingCartTranferOrders.Add(order);
            }
        }

        Console.WriteLine($"INFO Phase=WholesalePendingCartExit Event=CandidatesSelected Count={PendingCartTranferOrders.Count}");

        foreach (var order in PendingCartTranferOrders)
        {
            try
            {
            //Elliminating any wholesale order.
            if (order.IsWholesale == false)
            {
                Console.WriteLine($"INFO Phase=WholesalePendingCartExit Event=OrderSkipped Reason=RetailOrder OrderId={order.InternalId} OrderNumber={order.OrderNumber}");
                continue;
            }

            //Eliminate and order that does not have complete item data.
            if (order.HasIncompleteItemData)
            {
                Console.WriteLine($"WARN Phase=WholesalePendingCartExit Event=OrderSkipped Reason=IncompleteItemData OrderId={order.InternalId} OrderNumber={order.OrderNumber}");
                continue;
            }

            //Updating any order which is fully commited to Wholesale - Approved.
            if (order.UncommittedItems.Count == 0)
            {
                Console.WriteLine($"INFO Phase=WholesalePendingCartExit Event=OrderQualified Reason=FullyCommitted TargetStatusId={WsApprovedStatusId} OrderId={order.InternalId} OrderNumber={order.OrderNumber}");
                await WsOrderUpdateById(http, baseUrl, WsApprovedStatusId, order.InternalId);
                continue;
            }

            //Checking for first or second shipment
            if (order.FirstShipment == true)
            {
                Console.WriteLine($"INFO Phase=WholesalePendingCartExit Event=DecisionPath Path=FirstShipment OrderId={order.InternalId} OrderNumber={order.OrderNumber} UncommittedLines={order.UncommittedItems.Count}");
                await WsOrderFirstShipmentDecisionPath(order, itemInventoryData, http, baseUrl, WsApprovedStatusId);
            }
            else
            {
                Console.WriteLine($"INFO Phase=WholesalePendingCartExit Event=DecisionPath Path=SecondShipment OrderId={order.InternalId} OrderNumber={order.OrderNumber} UncommittedLines={order.UncommittedItems.Count}");
                await WsOrderSecondShipmentDecisionPath(order, itemInventoryData, http, baseUrl, WsBackorderStatusId);
            }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"ERROR Phase=WholesalePendingCartExit Event=OrderProcessingFailed OrderId={order.InternalId} OrderNumber={order.OrderNumber} ExceptionType={ex.GetType().Name} Message={ex.Message}");
            }
        }
    }

    private static async Task WsOrderFirstShipmentDecisionPath(SO order, List<ItemInventory> itemInventoryData, HttpClient http, string baseUrl, string WsApprovedId)
    {
        //First Shipment Path
        //If at least one item on the SO is in pending status, do nothing
        //If all stock is Available, then change status to Wholesale - Approved Status
        bool isAllAvailable = true;
        foreach (var item in order.UncommittedItems)
        {
            ItemInventory? matchingInventory = itemInventoryData.Find(inventory => inventory.ItemInternalId == item.ItemInternalId);
            if (matchingInventory == null || matchingInventory.RetrievalSucceeded == false)
            {
                //Skipping any item with no inventory data since we cannot make a guess to which status it should be in.
                Console.WriteLine($"WARN Phase=WholesalePendingCartExit Event=OrderSkipped Reason=MissingInventoryData Path=FirstShipment OrderId={order.InternalId} OrderNumber={order.OrderNumber} ItemId={item.ItemInternalId}");
                isAllAvailable = false;
                break;
            }

            //All items must be available for this so any one non available status will make this not continue.
            foreach (var balance in matchingInventory.Balances)
            {
                if (balance.InventoryStatusId != 1 && balance.QuantityOnHand > 0)
                {
                    Console.WriteLine($"INFO Phase=WholesalePendingCartExit Event=OrderSkipped Reason=PositiveNonAvailableInventory Path=FirstShipment OrderId={order.InternalId} OrderNumber={order.OrderNumber} ItemId={item.ItemInternalId} LocationId={balance.LocationId} InventoryStatusId={balance.InventoryStatusId} Quantity={balance.QuantityOnHand}");
                    isAllAvailable = false;
                    break;
                }
            }
        }

        //If every item for an SO is available the SO
        // can be set to Approved.
        if(isAllAvailable)
        {
            Console.WriteLine($"INFO Phase=WholesalePendingCartExit Event=OrderQualified Path=FirstShipment TargetStatusId={WsApprovedId} OrderId={order.InternalId} OrderNumber={order.OrderNumber}");
            await WsOrderUpdateById(http, baseUrl, WsApprovedId, order.InternalId);
        }
    }

    private static async Task WsOrderSecondShipmentDecisionPath(SO order, List<ItemInventory> itemInventoryData, HttpClient http, string baseUrl, string WsBackorderId)
    {
        //Second Shipment Path
        //If at least one item on the SO is in pending status, do nothing.
        //If all items on the SO is available, change status to Wholsesale - Backorder
        bool isAllAvailable = true;
        foreach (var item in order.UncommittedItems)
        {
            ItemInventory? matchingInventory = itemInventoryData.Find(inventory => inventory.ItemInternalId == item.ItemInternalId);
            if (matchingInventory == null || matchingInventory.RetrievalSucceeded == false)
            {
                //Skipping any item with no inventory data since we cannot make a guess to which status it should be in.
                Console.WriteLine($"WARN Phase=WholesalePendingCartExit Event=OrderSkipped Reason=MissingInventoryData Path=SecondShipment OrderId={order.InternalId} OrderNumber={order.OrderNumber} ItemId={item.ItemInternalId}");
                isAllAvailable = false;
                break;
            }

            //All items must be available for this so any one non available status will make this not continue.
            foreach (var balance in matchingInventory.Balances)
            {
                if (balance.InventoryStatusId != 1 && balance.QuantityOnHand > 0)
                {
                    Console.WriteLine($"INFO Phase=WholesalePendingCartExit Event=OrderSkipped Reason=PositiveNonAvailableInventory Path=SecondShipment OrderId={order.InternalId} OrderNumber={order.OrderNumber} ItemId={item.ItemInternalId} LocationId={balance.LocationId} InventoryStatusId={balance.InventoryStatusId} Quantity={balance.QuantityOnHand}");
                    isAllAvailable = false;
                    break;
                }
            }
        }

        //If every item for an SO is available the SO
        // can be set to Approved.
        if (isAllAvailable)
        {
            Console.WriteLine($"INFO Phase=WholesalePendingCartExit Event=OrderQualified Path=SecondShipment TargetStatusId={WsBackorderId} OrderId={order.InternalId} OrderNumber={order.OrderNumber}");
            await WsOrderUpdateById(http, baseUrl, WsBackorderId, order.InternalId);
        }
    }
    private static async Task WsOrderUpdateById(HttpClient http, string baseUrl, string id, long orderInternalId)
    {
        try
        {
            var payload = new
            {
                custbody_f_customer_order_status = new
                {
                    id = id
                },
            };

            using var request = new HttpRequestMessage(HttpMethod.Patch, $"{baseUrl}/services/rest/record/v1/salesOrder/{orderInternalId}");
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using HttpResponseMessage response = await http.SendAsync(request);

            await ReadSuccessfulResponseAsync(response, $"Updating sales order {orderInternalId} to wholesale status id {id}.");
            Console.WriteLine($"INFO Phase=WholesalePendingCartExit Event=OrderUpdated OrderId={orderInternalId} TargetStatusId={id} HttpStatus={(int)response.StatusCode}");

        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR Phase=WholesalePendingCartExit Event=OrderUpdateFailed OrderId={orderInternalId} TargetStatusId={id} ExceptionType={ex.GetType().Name} Message={ex.Message}");
            throw;
        }
    }

    private static async Task MovePentingCartTransferOrdersToBackorderPendingReviewRetail(List<SO> orders, List<ItemInventory> itemInventoryData, HttpClient http, string baseUrl)
    {
        var PendingCartTranferOrders = new List<SO>();
        string PendingCartTransferId = Required("NETSUITE_STATUS_CUSTOMER_ORDER");
        string BackorderPendingReviewId = Required("NETSUITE_STATUS_BACKORDER_PENDING");

        //This will reduce our list to only orders that are in the Pending Cart Transfer status.
        foreach (var order in orders)
        {
            if (order.CustomerOrderStatusId == PendingCartTransferId)
            {
                PendingCartTranferOrders.Add(order);
            }
        }

        Console.WriteLine($"INFO Phase=RetailPendingCartExit Event=CandidatesSelected Count={PendingCartTranferOrders.Count}");

        //Looping through all of the elligible orders and skipping them if they meet and disqualifying criteria.
        //If they make it to the end, they will be updated in netsuite.
        foreach(var order in PendingCartTranferOrders)
        {
            try
            {
            //Elliminating any wholesale order.
            if (order.IsWholesale == true)
            {
                Console.WriteLine($"INFO Phase=RetailPendingCartExit Event=OrderSkipped Reason=WholesaleOrder OrderId={order.InternalId} OrderNumber={order.OrderNumber}");
                continue;
            }

            //Eliminate and order that does not have complete item data.
            if (order.HasIncompleteItemData)
            {
                Console.WriteLine($"WARN Phase=RetailPendingCartExit Event=OrderSkipped Reason=IncompleteItemData OrderId={order.InternalId} OrderNumber={order.OrderNumber}");

                continue;
            }

            //Eliminating any order which is fully commited.
            if (order.UncommittedItems.Count == 0)
            {
                Console.WriteLine($"INFO Phase=RetailPendingCartExit Event=OrderSkipped Reason=FullyCommittedWorkflowWillHandle OrderId={order.InternalId} OrderNumber={order.OrderNumber}");
                continue;
            }

            bool shouldContinue = true;
            foreach(var item in order.UncommittedItems)
            {
                ItemInventory? matchingInventory = itemInventoryData.Find(inventory => inventory.ItemInternalId == item.ItemInternalId);
                if(matchingInventory == null || matchingInventory.RetrievalSucceeded == false)
                {
                    //Skipping any item with no inventory data since we cannot make a guess to which status it should be in.
                    Console.WriteLine($"WARN Phase=RetailPendingCartExit Event=OrderSkipped Reason=MissingInventoryData OrderId={order.InternalId} OrderNumber={order.OrderNumber} ItemId={item.ItemInternalId}");
                    shouldContinue = false;
                    break;
                }

                //If any one item is pending do nothing.
                foreach(var balance in matchingInventory.Balances)
                {
                    if(balance.InventoryStatusId == 4 && balance.QuantityOnHand > 0)
                    {
                        Console.WriteLine($"INFO Phase=RetailPendingCartExit Event=OrderSkipped Reason=PositivePendingInventory OrderId={order.InternalId} OrderNumber={order.OrderNumber} ItemId={item.ItemInternalId} LocationId={balance.LocationId} Quantity={balance.QuantityOnHand}");
                        shouldContinue = false;
                        break;
                    }
                }

                //All items must be available for this so any one non available status will make this not continue.
                foreach (var balance in matchingInventory.Balances)
                {
                    if (balance.InventoryStatusId != 1 && balance.QuantityOnHand > 0)
                    {
                        Console.WriteLine($"INFO Phase=RetailPendingCartExit Event=OrderSkipped Reason=PositiveNonAvailableInventory OrderId={order.InternalId} OrderNumber={order.OrderNumber} ItemId={item.ItemInternalId} LocationId={balance.LocationId} InventoryStatusId={balance.InventoryStatusId} Quantity={balance.QuantityOnHand}");
                        shouldContinue = false;
                        break;
                    }
                }
            }

            //After all of those checks, if should continue remains true, 
            //then we can update the SO to Backorder and set ready to fulfill to false
            if(!shouldContinue)
            {
                continue;
            }

            //Send patch to SO to update the fields.
            Console.WriteLine($"INFO Phase=RetailPendingCartExit Event=OrderQualified TargetStatusId={BackorderPendingReviewId} OrderId={order.InternalId} OrderNumber={order.OrderNumber} UncommittedLines={order.UncommittedItems.Count}");
            await UpdateOrderToBackorderPendingReview(http, baseUrl, BackorderPendingReviewId, order.InternalId);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"ERROR Phase=RetailPendingCartExit Event=OrderProcessingFailed OrderId={order.InternalId} OrderNumber={order.OrderNumber} ExceptionType={ex.GetType().Name} Message={ex.Message}");
            }
        }
    }
    private static async Task UpdateOrderToBackorderPendingReview(HttpClient http, string baseUrl, string BackorderPendingReviewId, long orderInternalId)
    {
        try 
        { 
            var payload = new
            {
                custbody_f_customer_order_status = new
                {
                    id = BackorderPendingReviewId
                },
                custbody_f_ready_to_fulfill = false
            };

            using var request = new HttpRequestMessage(HttpMethod.Patch, $"{baseUrl}/services/rest/record/v1/salesOrder/{orderInternalId}");
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using HttpResponseMessage response = await http.SendAsync(request);

            await ReadSuccessfulResponseAsync(response, $"Updating sales order {orderInternalId} to Backorder Pending Review.");
            Console.WriteLine($"INFO Phase=RetailPendingCartExit Event=OrderUpdated OrderId={orderInternalId} TargetStatusId={BackorderPendingReviewId} ReadyToFulfill=false HttpStatus={(int)response.StatusCode}");

        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR Phase=RetailPendingCartExit Event=OrderUpdateFailed OrderId={orderInternalId} TargetStatusId={BackorderPendingReviewId} ExceptionType={ex.GetType().Name} Message={ex.Message}");
            throw;
        }
    }

    private static async Task MoveOrdersToPendingCartTransfer(List<SO> orders, List<ItemInventory> itemInventoryData, HttpClient http, string baseUrl)
    {
        var nonPendingCartTranferOrders = new List<SO>();
        string PendingCartTransferId = Required("NETSUITE_STATUS_CUSTOMER_ORDER");

        //Creating a new order list to reduce the amount to only the qualifying order status.
        //This will leave us with every order not in the "Pending Cart Transfer" to be evaluated and potentally moved to it.
        foreach(var order in orders)
        {
            if(order.CustomerOrderStatusId != PendingCartTransferId)
            {
                nonPendingCartTranferOrders.Add(order);
            }
        }

        Console.WriteLine($"INFO Phase=PendingCartEntry Event=CandidatesSelected Count={nonPendingCartTranferOrders.Count}");

        foreach(var order in nonPendingCartTranferOrders)
        {
            try
            {
            //Checking for inventory data and skipping the SO if any item on it has no corresponsing inventory data.
            //This is done to avoid making decisions on not existing data.
            if (order.HasIncompleteItemData == true)
            {
                Console.WriteLine($"WARN Phase=PendingCartEntry Event=OrderSkipped Reason=IncompleteItemData OrderId={order.InternalId} OrderNumber={order.OrderNumber}");
                continue;
            }

            //If no uncommitted items exist it should not be placed into this status
            if(order.UncommittedItems.Count == 0)
            {
                Console.WriteLine($"INFO Phase=PendingCartEntry Event=OrderSkipped Reason=NoUncommittedLines OrderId={order.InternalId} OrderNumber={order.OrderNumber}");
                continue;
            }

            bool shouldContinueToStatusChange = true;
            bool hasQualifyingPendingItem = false;

            foreach (var item in order.UncommittedItems)
            {
                ItemInventory? matchingInventory = itemInventoryData.Find(inventory => inventory.ItemInternalId == item.ItemInternalId);

                if (matchingInventory == null || matchingInventory.RetrievalSucceeded == false)
                {
                    shouldContinueToStatusChange = false;
                    Console.WriteLine($"WARN Phase=PendingCartEntry Event=OrderSkipped Reason=MissingInventoryData OrderId={order.InternalId} OrderNumber={order.OrderNumber} ItemId={item.ItemInternalId}");
                    break;
                }

                if(matchingInventory.ProShopQuantityOnHand > 0)
                {
                    shouldContinueToStatusChange = false;
                    Console.WriteLine($"INFO Phase=PendingCartEntry Event=OrderSkipped Reason=PositiveProShopInventory OrderId={order.InternalId} OrderNumber={order.OrderNumber} ItemId={item.ItemInternalId} ProShopQuantity={matchingInventory.ProShopQuantityOnHand}");
                    break;
                }

                //If this is greater than 0 then we have an uncommitted available quanitity somewhere(A cart) which means we should keep moving
                if((matchingInventory.TotalQuantityOnHand - matchingInventory.OnHoldInventory) > 0)
                {
                    hasQualifyingPendingItem = true;
                }
            }

            //At this point if our bool is still true then an item is on a cart and uncommitted.
            if (!shouldContinueToStatusChange || !hasQualifyingPendingItem)
            {
                Console.WriteLine($"INFO Phase=PendingCartEntry Event=OrderSkipped Reason=NoQualifyingCartInventory OrderId={order.InternalId} OrderNumber={order.OrderNumber} UncommittedLines={order.UncommittedItems.Count}");

                continue;
            }

            Console.WriteLine($"INFO Phase=PendingCartEntry Event=OrderQualified TargetStatusId={PendingCartTransferId} OrderId={order.InternalId} OrderNumber={order.OrderNumber} UncommittedLines={order.UncommittedItems.Count}");
            //Since the order is awaiting a cart transfer, we need to change the status
            //  and then uncheck ready to fulfill so the order does not get picked.
            await UpdateOrderToPendingCartTransferAndUncheckReadyToFulfill(http, baseUrl, order.InternalId, PendingCartTransferId);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"ERROR Phase=PendingCartEntry Event=OrderProcessingFailed OrderId={order.InternalId} OrderNumber={order.OrderNumber} ExceptionType={ex.GetType().Name} Message={ex.Message}");
            }
        }
    }

    private static async Task UpdateOrderToPendingCartTransferAndUncheckReadyToFulfill(HttpClient http, string baseUrl, long orderInternalId, string pendingCartTransferId)
    {
        try
        {
            var payload = new
            {
                custbody_f_customer_order_status = new
                {
                    id = pendingCartTransferId
                },

                custbody_f_ready_to_fulfill = false
            };

            using var request = new HttpRequestMessage(HttpMethod.Patch, $"{baseUrl}/services/rest/record/v1/salesOrder/{orderInternalId}");
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using HttpResponseMessage response = await http.SendAsync(request);

            await ReadSuccessfulResponseAsync(response, $"Updating sales order {orderInternalId} to Pending Cart Transfer");
            Console.WriteLine($"INFO Phase=PendingCartEntry Event=OrderUpdated OrderId={orderInternalId} TargetStatusId={pendingCartTransferId} ReadyToFulfill=false HttpStatus={(int)response.StatusCode}");

        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR Phase=PendingCartEntry Event=OrderUpdateFailed OrderId={orderInternalId} TargetStatusId={pendingCartTransferId} ExceptionType={ex.GetType().Name} Message={ex.Message}");
            throw;
        }
    }

    private static async Task RemovingUnwantedItemLinesFromList(List<SO> orders, HashSet<long> idsToRemove)
    {
        foreach (var order in orders)
        {
            order.Items.RemoveAll(item => idsToRemove.Contains(item.ItemInternalId));

            // Refresh this list so it no longer contains removed items.
            order.RefreshUncommittedItems();
        }
    }

    private static async Task<List<ItemInventory>> GetItemInventoryAsync(HttpClient http, string baseUrl, HashSet<long> itemIds, long proShopLocationId)
    {
        var items = new List<ItemInventory>();

        if(itemIds.Count == 0)
        {
            return items;
        }

        foreach (long[] batch in System.Linq.Enumerable.Chunk(itemIds, 500))
        {
            string ids = string.Join(",", batch);
            Console.WriteLine($"INFO Phase=InventoryRetrieval Event=BatchStarted ItemCount={batch.Length}");
            

            
            List<JsonElement> itemRows = await RunSuiteQlAsync(http, baseUrl, 
               $@"SELECT id, custitem_on_hold_inventory
               FROM Item
               WHERE id IN ({ids})
               ORDER BY id");

            List<JsonElement> locationRows = await RunSuiteQlAsync(http, baseUrl,
                $@"SELECT item, location, quantityonhand
               FROM AggregateItemLocation
               WHERE item IN ({ids})
               ORDER BY item, location");

            List<JsonElement> balanceRows = await RunSuiteQlAsync(http, baseUrl,
                $@"SELECT item, location, inventorystatus,
                SUM(quantityonhand) AS quantityonhand
                FROM InventoryBalance
                WHERE item IN ({ids})
                GROUP BY item, location, inventorystatus
                ORDER BY item, location, inventorystatus");


            foreach (long itemId in batch)
            {
                try
                {
                // Find the item record.
                JsonElement itemRow = itemRows.Find(row => ReadValue(row, "id") == itemId.ToString(System.Globalization.CultureInfo.InvariantCulture));

                if (itemRow.ValueKind == JsonValueKind.Undefined)
                {
                    throw new InvalidOperationException($"Item {itemId} was not returned by the item query.");
                }

                var item = new ItemInventory
                {
                    ItemInternalId = itemId,
                    OnHoldInventory = ReadInventoryQuantity(itemRow, "custitem_on_hold_inventory")
                };

                // Add quantities from each location.
                foreach (JsonElement row in locationRows)
                {
                    long rowItemId = long.Parse(ReadValue(row, "item"), System.Globalization.CultureInfo.InvariantCulture);

                    if (rowItemId != itemId)
                    {
                        continue;
                    }

                    long locationId = long.Parse(ReadValue(row, "location"), System.Globalization.CultureInfo.InvariantCulture);

                    decimal quantity = ReadInventoryQuantity(row, "quantityonhand");

                    item.TotalQuantityOnHand += quantity;

                    if (locationId == proShopLocationId)
                    {
                        item.ProShopQuantityOnHand += quantity;
                    }
                }

                // Store inventory by location and status.
                foreach (JsonElement row in balanceRows)
                {
                    long rowItemId = long.Parse(ReadValue(row, "item"), System.Globalization.CultureInfo.InvariantCulture);

                    if (rowItemId != itemId)
                    {
                        continue;
                    }

                    item.Balances.Add(new InventoryBalance
                    {
                        LocationId = long.Parse(ReadValue(row, "location"), System.Globalization.CultureInfo.InvariantCulture),

                        InventoryStatusId = long.Parse(ReadValue(row, "inventorystatus"), System.Globalization.CultureInfo.InvariantCulture),

                        QuantityOnHand = ReadInventoryQuantity(row, "quantityonhand")
                    });
                }

                item.RetrievalSucceeded = true;
                items.Add(item);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"ERROR Phase=InventoryRetrieval Event=ItemProcessingFailed ItemId={itemId} ExceptionType={ex.GetType().Name} Message={ex.Message}");
                }
            }

            Console.WriteLine($"INFO Phase=InventoryRetrieval Event=BatchCompleted ItemCount={batch.Length} TotalStored={items.Count}");
        }

        Console.WriteLine($"INFO Phase=InventoryRetrieval Event=StoredInventory ItemCount={items.Count}");

        return items;
    }
    private static decimal ReadInventoryQuantity(JsonElement row, string field)
    {
        string value = ReadValue(row, field, allowNull: true);

        return string.IsNullOrWhiteSpace(value)
            ? 0m
            : decimal.Parse(
                value,
                System.Globalization.CultureInfo.InvariantCulture);
    }
    private static async Task<List<JsonElement>> GetElligibleOrders(HttpClient http, string baseUrl)
    {
        var fields = new CustomFieldIds
        {
            CustomerOrderStatus = "custbody_f_customer_order_status",
            ReadyToFulfill = "custbody_f_ready_to_fulfill",
            FirstShipment = "custbody_first_shipment",
            WholesaleFlag = "custbody_f_shipping_category"
        };
        string[] names = { "PAYMENT_HOLD", "INCOMPLETE_PICK", "EXTERNAL_FRAUD_REVIEW", "ANGLER_SUPPORT_ON_HOLD", "WHOLESALE_ON_HOLD" };
        var excluded = new List<string>();
        foreach (string name in names)
        {
            long id = long.Parse(Required("NETSUITE_STATUS_" + name), System.Globalization.CultureInfo.InvariantCulture);
            if (id <= 0)
            {
                throw new InvalidOperationException("Status IDs must be positive.");
            }

            excluded.Add(id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        string query = $@"
            SELECT t.id AS orderid, t.tranid AS ordernumber,
                t.{fields.CustomerOrderStatus} AS customerorderstatusid,
                NVL(t.{fields.ReadyToFulfill}, 'F') AS readytofulfill,
                NVL(t.{fields.FirstShipment}, 'F') AS firstshipment,
                CASE WHEN t.{fields.WholesaleFlag} = 4 THEN 'T' ELSE 'F' END AS iswholesale,
                tl.id AS lineid, tl.item AS itemid, i.itemid AS itemname,
                i.itemtype AS itemtype, tl.location AS locationid,
                NVL(tl.isclosed, 'F') AS isclosed,
                tl.quantity AS quantityordered,
                NVL(tl.quantityshiprecv, 0) AS quantityshipped,
                NVL(tl.quantitycommitted, 0) AS quantitycommitted
            FROM transaction t
            LEFT JOIN transactionline tl ON tl.transaction = t.id
                AND tl.mainline = 'F' AND tl.taxline = 'F' AND tl.item IS NOT NULL
            LEFT JOIN item i ON i.id = tl.item
            WHERE t.type = 'SalesOrd'
                AND t.status NOT IN ('C', 'G', 'H')
                AND (t.{fields.CustomerOrderStatus} IS NULL
                    OR t.{fields.CustomerOrderStatus} NOT IN ({string.Join(",", excluded)}))
            ORDER BY t.id, tl.id";

        return await RunSuiteQlAsync(http, baseUrl, query);
    }

    private static async Task<List<JsonElement>> RunSuiteQlAsync(HttpClient http, string baseUrl, string query)
    {
        const int pageSize = 1000;
        var rows = new List<JsonElement>();
        for (int offset = 0; ; offset += pageSize)
        {
            if (offset >= 100000)
            {
                throw new InvalidOperationException("SuiteQL result limit reached; refusing incomplete orders.");
            }
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/services/rest/query/v1/suiteql?limit={pageSize}&offset={offset}");
            request.Headers.Add("Prefer", "transient");
            request.Content = new StringContent(JsonSerializer.Serialize(new { q = query }), Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request);
            string body = await ReadSuccessfulResponseAsync(response, $"Retrieving orders at offset {offset}");
            using var document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            JsonElement page = root.GetProperty("items");
            foreach (JsonElement row in page.EnumerateArray())
            {
                rows.Add(row.Clone());
            }
            Console.WriteLine($"INFO Phase=SuiteQL Event=PageRetrieved Offset={offset} PageRows={page.GetArrayLength()} TotalRows={rows.Count} HasMore={root.GetProperty("hasMore").GetBoolean()}");
            if (!root.GetProperty("hasMore").GetBoolean())
            {
                return rows;
            }

            if (page.GetArrayLength() == 0)
            {
                throw new InvalidOperationException("NetSuite reported more pages but returned no rows.");
            }
        }
    }

    private static void PopulateOrders(List<JsonElement> rows, List<SO> orders, Dictionary<long, SO> ordersById, HashSet<long> itemIdsToRetrieve)
    {
        // Parse locally before exposing results to the rest of the script.
        var parsed = new Dictionary<long, SO>();
        var lineKeys = new HashSet<(long OrderId, string LineId)>();
        foreach (JsonElement row in rows)
        {
            long orderId = long.Parse(ReadValue(row, "orderid"), System.Globalization.CultureInfo.InvariantCulture);
            if (!parsed.TryGetValue(orderId, out SO? order))
            {
                order = new SO
                {
                    InternalId = orderId,
                    OrderNumber = ReadValue(row, "ordernumber"),
                    CustomerOrderStatusId = ReadValue(row, "customerorderstatusid", true),
                    ReadyToFulfill = ReadCheckbox(row, "readytofulfill"),
                    IsWholesale = ReadCheckbox(row, "iswholesale"),
                    FirstShipment = ReadCheckbox(row, "firstshipment")
                };
                parsed.Add(orderId, order);
            }
            else if (order.CustomerOrderStatusId != ReadValue(row, "customerorderstatusid", true) || order.ReadyToFulfill != ReadCheckbox(row, "readytofulfill") || order.IsWholesale != ReadCheckbox(row, "iswholesale")
                || order.FirstShipment != ReadCheckbox(row, "firstshipment"))
            {
                throw new InvalidOperationException($"Order {orderId} changed during retrieval; rerun.");
            }
            string itemId = ReadValue(row, "itemid", true);

            if (itemId.Length == 0)
            {
                continue;
            }

            string lineId = ReadValue(row, "lineid");

            if (!lineKeys.Add((orderId, lineId)))
            {
                throw new InvalidOperationException($"Duplicate line {lineId} on order {orderId}.");
            }
            bool isShippingItem = ShippingItemIds.Contains(long.Parse(itemId, System.Globalization.CultureInfo.InvariantCulture));
            string type = ReadValue(row, "itemtype", true);
            string itemName = ReadValue(row, "itemname", true);
            if (string.IsNullOrWhiteSpace(type) && !isShippingItem)
            {
                order.HasIncompleteItemData = true;
                Console.WriteLine($"WARN Phase=OrderParsing Event=IncompleteItemData Reason=MissingItemType OrderId={orderId} OrderNumber={order.OrderNumber} LineId={lineId} ItemId={itemId}");
            }
            string location = ReadValue(row, "locationid", true);
            bool commitment = !isShippingItem && (type == "InvtPart" || type == "Assembly");
            order.Items.Add(new OrderItem
            {
                LineId = lineId,
                ItemInternalId = long.Parse(itemId, System.Globalization.CultureInfo.InvariantCulture),
                ItemName = itemName, ItemType = type,
                LocationId = location.Length == 0 ? null : long.Parse(location, System.Globalization.CultureInfo.InvariantCulture),
                IsClosed = ReadCheckbox(row, "isclosed"), RequiresInventoryCommitment = commitment,
                // Normalize signed analytics quantities to magnitudes in the same base units.
                QuantityOrdered = ReadQuantity(row, "quantityordered", !commitment),
                QuantityShipped = ReadQuantity(row, "quantityshipped", !commitment),
                QuantityCommitted = ReadQuantity(row, "quantitycommitted", !commitment)
            });
        }
        foreach (SO order in parsed.Values)
        {
            order.RefreshUncommittedItems();
        }
        foreach (SO order in parsed.Values)
        {
            ordersById.Add(order.InternalId, order);
            orders.Add(order);
            foreach (OrderItem item in order.UncommittedItems)
            {
                itemIdsToRetrieve.Add(item.ItemInternalId);
            }
        }
        Console.WriteLine($"INFO Phase=OrderParsing Event=Completed Orders={orders.Count} ItemLines={lineKeys.Count} UniqueUncommittedItems={itemIdsToRetrieve.Count}");
    }

    private static string ReadValue(JsonElement row, string name, bool allowNull = false)
    {
        foreach (JsonProperty property in row.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (property.Value.ValueKind != JsonValueKind.Null)
            {
                return property.Value.ToString();
            }
            if (allowNull)
            {
                return string.Empty;
            }
            throw new InvalidOperationException($"Required query value {name} is null.");
        }
        // SuiteQL may omit null properties.
        if (allowNull)
        {
            return string.Empty;
        }
        throw new InvalidOperationException($"Required query value {name} is missing.");
    }

    private static bool ReadCheckbox(JsonElement row, string name)
    {
        string value = ReadValue(row, name);
        if (value.Equals("T", StringComparison.OrdinalIgnoreCase) || value.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (value.Equals("F", StringComparison.OrdinalIgnoreCase) || value.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        throw new InvalidOperationException($"Unexpected checkbox value for {name}: {value}");
    }

    private static decimal ReadQuantity(JsonElement row, string name, bool allowNull)
    {
        string value = ReadValue(row, name, allowNull);
        return value.Length == 0 ? 0m : Math.Abs(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture));
    }

    private static async Task AuthenticateAsync(HttpClient http, string baseUrl, string clientId, string certificateId, string privateKeyPath)
    {
        Console.WriteLine("INFO Phase=Authentication Event=Started");

        string tokenUrl = $"{baseUrl}/services/rest/auth/oauth2/v1/token";
        string signedJwt = await CreateSignedJwtAsync(tokenUrl, clientId, certificateId, privateKeyPath);

        using var tokenForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
            ["client_assertion"] = signedJwt
        });

        using var response = await http.PostAsync(tokenUrl, tokenForm);
        string body = await ReadSuccessfulResponseAsync(response, "Authentication");

        using var tokenJson = JsonDocument.Parse(body);

        string accessToken = tokenJson.RootElement.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("NetSuite did not return an access token.");

        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        Console.WriteLine("INFO Phase=Authentication Event=Completed");
    }

    private static async Task<string> CreateSignedJwtAsync(string tokenUrl, string clientId, string certificateId, string privateKeyPath)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var header = new
        {
            alg = "PS256",
            typ = "JWT",
            kid = certificateId
        };

        var payload = new
        {
            iss = clientId,
            scope = new[] { "rest_webservices" },
            aud = tokenUrl,
            iat = now,
            exp = now + 300,
            jti = Guid.NewGuid().ToString()
        };

        string encodedHeader = Base64Url(JsonSerializer.SerializeToUtf8Bytes(header));
        string encodedPayload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload));
        string unsignedJwt = $"{encodedHeader}.{encodedPayload}";

        using var rsa = RSA.Create();

        string privateKey = await File.ReadAllTextAsync(privateKeyPath);
        rsa.ImportFromPem(privateKey);

        byte[] signature = rsa.SignData(Encoding.UTF8.GetBytes(unsignedJwt), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);

        return $"{unsignedJwt}.{Base64Url(signature)}";
    }

    private static async Task<string> ReadSuccessfulResponseAsync(HttpResponseMessage response, string operation)
    {
        string body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"{operation} failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}{Environment.NewLine}{body}");
        }

        return body;
    }

    private static string Required(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Missing configuration: {name}");
        }

        return value.Trim();
    }

    private static string Base64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static void LoadEnvFile(string path)
    {
        // Cloud Run can supply environment variables directly.
        if (!File.Exists(path))
        {
            Console.WriteLine($"INFO Phase=Configuration Event=EnvFileNotFound Source=EnvironmentVariables Path={path}");
            return;
        }

        Console.WriteLine($"INFO Phase=Configuration Event=EnvFileLoading Path={path}");

        foreach (string rawLine in File.ReadLines(path))
        {
            string line = rawLine.Trim();

            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            int separator = line.IndexOf('=');

            if (separator <= 0)
            {
                continue;
            }

            string name = line[..separator].Trim();
            string value = line[(separator + 1)..].Trim();

            // Support single-quoted or double-quoted values.
            if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }

            // Existing environment variables take priority.
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        Console.WriteLine($"INFO Phase=Configuration Event=EnvFileLoaded Path={path}");
    }
}
