FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY OnHoldInventory.csproj ./
RUN dotnet restore OnHoldInventory.csproj

COPY . ./
RUN dotnet publish OnHoldInventory.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app

COPY --from=build /app/publish ./

USER app
ENTRYPOINT ["dotnet", "OnHoldInventory.dll"]