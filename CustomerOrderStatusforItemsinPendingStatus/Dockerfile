FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY CustomerOrderStatusforItemsInPendingStatus.csproj ./
RUN dotnet restore CustomerOrderStatusforItemsInPendingStatus.csproj

COPY . ./
RUN dotnet publish CustomerOrderStatusforItemsInPendingStatus.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app

COPY --from=build /app/publish ./

USER app
ENTRYPOINT ["dotnet", "CustomerOrderStatusforItemsInPendingStatus.dll"]