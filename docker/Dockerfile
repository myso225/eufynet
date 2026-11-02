# Runtime-only image for the Raspberry Pi 5 (arm64). The app is published on the PC first:
#   dotnet publish src/EufyNet.App -c Release -r linux-arm64 --self-contained false -o docker/app
# so building the image runs nothing and needs no emulation (build it on the Pi, or with buildx --platform linux/arm64).
FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY app/ .
ENV DOTNET_ENVIRONMENT=Production \
    Eufy__DataDir=/app/data
VOLUME /app/data
# compose sets user: to the owner of the data folder on the host.
USER $APP_UID
ENTRYPOINT ["dotnet", "EufyNet.App.dll"]
