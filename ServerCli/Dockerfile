# The server is an ASP.NET Core app, so it needs the aspnet image rather than the plain runtime.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
# Every referenced project's file has to be here for the restore to resolve, and copying only the
# project files keeps this layer cached until a dependency actually changes.
COPY ["ServerCli/ServerCli.csproj", "ServerCli/"]
COPY ["RaindropServer/RaindropServer.csproj", "RaindropServer/"]
COPY ["RaindropServer.Data.Sqlite/RaindropServer.Data.Sqlite.csproj", "RaindropServer.Data.Sqlite/"]
COPY ["RaindropServer.Data.Postgres/RaindropServer.Data.Postgres.csproj", "RaindropServer.Data.Postgres/"]
COPY ["RaindropServer.Data.SqlServer/RaindropServer.Data.SqlServer.csproj", "RaindropServer.Data.SqlServer/"]
RUN dotnet restore "ServerCli/ServerCli.csproj"
COPY . .
WORKDIR "/src/ServerCli"
RUN dotnet build "./ServerCli.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./ServerCli.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

# The TLS key and the SQLite file have to outlive the container. A regenerated certificate has a new
# fingerprint, and every client that pinned the old one would refuse to connect — so keep both on a
# volume mounted at /app/data.
ENV RAINDROP_URLS=https://0.0.0.0:5001 \
    RAINDROP_TLS_CERTIFICATE_PATH=/app/data/raindrop.pfx \
    RAINDROP_DB_CONNECTION_STRING="Data Source=/app/data/raindrop.db" \
    RAINDROP_ATTACHMENTS_PATH=/app/data/attachments
VOLUME /app/data
EXPOSE 5001

# Point at another database without rebuilding, e.g.
#   -e RAINDROP_DB_PROVIDER=postgres -e RAINDROP_DB_CONNECTION_STRING="Host=db;Database=raindrop;..." 

ENTRYPOINT ["dotnet", "ServerCli.dll"]
