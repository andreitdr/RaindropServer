# The server is an ASP.NET Core app, so it needs the aspnet image rather than the plain runtime.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
# The same side-by-side layout as a checkout: the server in /src/RaindropServer, the wire protocol it
# compiles in /src/RaindropProto. The protocol arrives as the named build context raindrop_proto —
# docker-compose.yml supplies it; building by hand, pass --build-context raindrop_proto=../RaindropProto.
WORKDIR /src/RaindropServer
COPY --from=raindrop_proto raindrop /src/RaindropProto/raindrop
# Every referenced project's file has to be here for the restore to resolve, and copying only the
# project files keeps this layer cached until a dependency actually changes.
COPY ["ServerCli/ServerCli.csproj", "ServerCli/"]
COPY ["RaindropServer/RaindropServer.csproj", "RaindropServer/"]
COPY ["RaindropServer.Grpc/RaindropServer.Grpc.csproj", "RaindropServer.Grpc/"]
COPY ["RaindropServer.Voice.LiveKit/RaindropServer.Voice.LiveKit.csproj", "RaindropServer.Voice.LiveKit/"]
COPY ["RaindropServer.Data.Sqlite/RaindropServer.Data.Sqlite.csproj", "RaindropServer.Data.Sqlite/"]
COPY ["RaindropServer.Data.Postgres/RaindropServer.Data.Postgres.csproj", "RaindropServer.Data.Postgres/"]
COPY ["RaindropServer.Data.SqlServer/RaindropServer.Data.SqlServer.csproj", "RaindropServer.Data.SqlServer/"]
RUN dotnet restore "ServerCli/ServerCli.csproj"
COPY . .
WORKDIR "/src/RaindropServer/ServerCli"
RUN dotnet build "./ServerCli.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./ServerCli.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

# The TLS certificate, the server identity key and the SQLite file have to outlive the container. A
# regenerated certificate has a new fingerprint, and a regenerated identity key is a stranger to every
# client that pinned the old one — so keep all of them on a volume mounted at /app/data. The identity
# key needs its own setting: left alone it sits next to the executable, in the container's own layer,
# and is lost the next time the container is recreated (a new image, a changed environment).
# SERVERCLI_HAS_PROBE tells deploy.sh this image has `ServerCli probe`, which it runs inside the
# container to read the server's identity key.
ENV SERVERCLI_HAS_PROBE=1 \
    RAINDROP_URLS=https://0.0.0.0:5001 \
    RAINDROP_TLS_CERTIFICATE_PATH=/app/data/raindrop.pfx \
    RAINDROP_TLS_IDENTITY_KEY_PATH=/app/data/server-identity.key \
    RAINDROP_DB_CONNECTION_STRING="Data Source=/app/data/raindrop.db" \
    RAINDROP_ATTACHMENTS_PATH=/app/data/attachments
# Created here, owned by the app user, so a named or anonymous volume (which copies the image's
# ownership) is writable by the non-root server. A bind mount still needs the right owner on the host,
# which deploy.sh fixes.
USER root
RUN mkdir -p /app/data && chown "$APP_UID:$APP_UID" /app/data
USER $APP_UID
VOLUME /app/data
EXPOSE 5001

# Point at another database without rebuilding, e.g.
#   -e RAINDROP_DB_PROVIDER=postgres -e RAINDROP_DB_CONNECTION_STRING="Host=db;Database=raindrop;..." 

ENTRYPOINT ["dotnet", "ServerCli.dll"]
