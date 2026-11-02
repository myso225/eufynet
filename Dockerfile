# EufyNet image. Builds from source, so no .NET SDK is needed on the host:
#   docker build -t eufynet .
#   docker buildx build --platform linux/arm64,linux/amd64 -t eufynet .
# The build stage runs on the build machine's own platform and cross-publishes for the target (no emulation).

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

# Restore first, so changing code does not invalidate the package layer.
COPY nuget.config ./
COPY src/EufyNet.Cloud/EufyNet.Cloud.csproj src/EufyNet.Cloud/
COPY src/EufyNet.P2P/EufyNet.P2P.csproj src/EufyNet.P2P/
COPY src/EufyNet.Core/EufyNet.Core.csproj src/EufyNet.Core/
COPY src/EufyNet.Transport.Console/EufyNet.Transport.Console.csproj src/EufyNet.Transport.Console/
COPY src/EufyNet.Transport.Mqtt/EufyNet.Transport.Mqtt.csproj src/EufyNet.Transport.Mqtt/
COPY src/EufyNet.App/EufyNet.App.csproj src/EufyNet.App/
RUN dotnet restore src/EufyNet.App/EufyNet.App.csproj -a $TARGETARCH

COPY src/ src/
RUN dotnet publish src/EufyNet.App/EufyNet.App.csproj -c Release -a $TARGETARCH --self-contained false --no-restore -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
ENV DOTNET_ENVIRONMENT=Production \
    Eufy__DataDir=/app/data
VOLUME /app/data
# Non-root; compose sets user: to the owner of the data folder on the host.
USER $APP_UID
ENTRYPOINT ["dotnet", "EufyNet.App.dll"]
