# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:11.0.100-resolute@sha256:65f3c7c6a25a239ab3e6719f2896147e7b68242ff2d8818dd70498bde6ef8bea AS build

ARG BUILD_CONFIG=release
ARG CI=true
ARG COMMIT_HASH
ARG SOURCE_DATE_EPOCH
ARG TARGETARCH

WORKDIR /nethermind

COPY src/Nethermind src/Nethermind
COPY Directory.*.props .
COPY Directory.Build.targets global.json nuget.config ./
COPY tools/LightClient/trim-publish.sh tools/LightClient/

# The source-only build context has no Git repository for Source Link to query.
RUN arch=$([ "$TARGETARCH" = "amd64" ] && echo "x64" || echo "$TARGETARCH") && \
  dotnet restore src/Nethermind/Nethermind.LightClient/Nethermind.LightClient.csproj -a "$arch" -p:SaveDiskSpace=true && \
  dotnet publish src/Nethermind/Nethermind.LightClient/Nethermind.LightClient.csproj \
    -c "$BUILD_CONFIG" -a "$arch" -o /publish --no-restore --no-self-contained \
    -p:SaveDiskSpace=true -p:SourceRevisionId="$COMMIT_HASH" \
    -p:EnableSourceControlManagerQueries=false -p:EnableSourceLink=false -warnaserror && \
  sh tools/LightClient/trim-publish.sh /publish

FROM mcr.microsoft.com/dotnet/aspnet:11.0.0-resolute@sha256:bc0d6653979efba491ab3d6f589d691f96c309c3c3ef81325efa6978e7b7f242

ARG COMMIT_HASH=unknown
ARG VERSION=1.0.1
ARG BUILD_TIMESTAMP=1970-01-01T00:00:00Z

LABEL org.opencontainers.image.title="Nethermind Light Client" \
  org.opencontainers.image.description="Ethereum light client with verified P2P state and local JSON-RPC." \
  org.opencontainers.image.vendor="Demerzel Solutions Limited" \
  org.opencontainers.image.licenses="LGPL-3.0-only" \
  org.opencontainers.image.url="https://github.com/NethermindEth/nethermind/tree/master/tools/LightClient" \
  org.opencontainers.image.documentation="https://github.com/NethermindEth/nethermind/blob/master/tools/LightClient/README.md" \
  org.opencontainers.image.source="https://github.com/NethermindEth/nethermind" \
  org.opencontainers.image.version="$VERSION" \
  org.opencontainers.image.revision="$COMMIT_HASH" \
  org.opencontainers.image.created="$BUILD_TIMESTAMP"

RUN apt-get update && \
  apt-get install -y --no-install-recommends curl && \
  curl -fsSL https://packages.microsoft.com/config/ubuntu/26.04/packages-microsoft-prod.deb -o /tmp/packages-microsoft-prod.deb && \
  echo "b2e7c6b9328e0c6a68bfe5d0045be90ceb192b32e0ca906c6c529b9012726f39  /tmp/packages-microsoft-prod.deb" | sha256sum -c - && \
  dpkg -i /tmp/packages-microsoft-prod.deb && \
  rm /tmp/packages-microsoft-prod.deb && \
  apt-get update && \
  apt-get install -y --no-install-recommends libmsquic=2.6.3 && \
  rm -rf /var/lib/apt/lists/* && \
  install -d -o "$APP_UID" -g "$APP_UID" -m 700 /data

WORKDIR /nethermind-light-client

ENV AllowedHosts="localhost;127.0.0.1;[::1];light-client" \
  ASPNETCORE_HTTP_PORTS=""

COPY --from=build /publish .
COPY tools/LightClient/docker-entrypoint.sh .

RUN chmod +x docker-entrypoint.sh

USER $APP_UID

VOLUME /data

EXPOSE 8545 30303 30303/udp 9050 9050/udp 9051/udp

HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
  CMD curl -fsS --max-time 4 http://127.0.0.1:8545/status > /dev/null || exit 1

ENTRYPOINT ["./docker-entrypoint.sh"]
