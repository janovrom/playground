# syntax=docker/dockerfile:1
# One image: native Linux .NET SDK + Wine + Windows .NET SDK (unzipped into a pre-initialised prefix).
FROM mcr.microsoft.com/dotnet/sdk:10.0
ARG DOTNET_SDK_VERSION=10.0.112

# Ubuntu's wine64 dispatches on argv[0]: link it as "wine64", not "wine".
RUN apt-get update \
    && apt-get install -y --no-install-recommends wine64 unzip ca-certificates curl \
    && rm -rf /var/lib/apt/lists/* \
    && ln -s /usr/lib/wine/wine64 /usr/local/bin/wine64 \
    && ln -s /usr/lib/wine/wineserver64 /usr/local/bin/wineserver

# Never set WINEDLLOVERRIDES="mscoree,mshtml=": it breaks CoreCLR assembly loading under Wine.
ENV WINEDEBUG=-all WINEARCH=win64 WINEPREFIX=/opt/wine/prefix \
    NUGET_PACKAGES=/root/.nuget/packages DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 MSBUILDDISABLENODEREUSE=1 \
    EnableWindowsTargeting=true

RUN mkdir -p "${WINEPREFIX}" && (wine64 wineboot --init 2>&1 || true) \
    && wineserver -k; test -f "${WINEPREFIX}/system.reg"

RUN curl -fsSL -o /tmp/sdk.zip "https://builds.dotnet.microsoft.com/dotnet/Sdk/${DOTNET_SDK_VERSION}/dotnet-sdk-${DOTNET_SDK_VERSION}-win-x64.zip" \
    && mkdir -p "${WINEPREFIX}/drive_c/dotnet" \
    && unzip -q /tmp/sdk.zip -d "${WINEPREFIX}/drive_c/dotnet" \
    && rm -f /tmp/sdk.zip

COPY entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/entrypoint.sh
WORKDIR /src
ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
