FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY CustomerOrderStatusforItemsinPendingStatus.csproj ./
RUN dotnet restore CustomerOrderStatusforItemsinPendingStatus.csproj

COPY . ./
RUN dotnet publish CustomerOrderStatusforItemsinPendingStatus.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app

COPY --from=build /app/publish ./

USER app
ENTRYPOINT ["dotnet", "CustomerOrderStatusforItemsinPendingStatus.dll"]