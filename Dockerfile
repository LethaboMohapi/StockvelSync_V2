FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY global.json ./
COPY src/StokvelSync.Web/StokvelSync.Web.csproj src/StokvelSync.Web/
RUN dotnet restore src/StokvelSync.Web/StokvelSync.Web.csproj
COPY src/StokvelSync.Web/ src/StokvelSync.Web/
RUN dotnet publish src/StokvelSync.Web/StokvelSync.Web.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
COPY scripts/docker-entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["/entrypoint.sh"]
