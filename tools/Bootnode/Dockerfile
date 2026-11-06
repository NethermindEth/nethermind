# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:11.0.100-rc.1-resolute@sha256:ab11199f8a0cded1d667111a0d4f0906567c3a17348a7181f9781d12cc5eb6f9 AS build

ARG BUILD_CONFIG=release
ARG CI=true
ARG COMMIT_HASH
ARG SOURCE_DATE_EPOCH
ARG TARGETARCH

WORKDIR /nethermind

COPY src/Nethermind src/Nethermind
COPY tools/Bootnode tools/Bootnode
COPY tools/Directory.Build.props tools/Directory.Build.props
COPY Directory.*.props .
COPY Directory.Build.targets .
COPY global.json .
COPY nuget.config .

RUN arch=$([ "$TARGETARCH" = "amd64" ] && echo "x64" || echo "$TARGETARCH") && \
  dotnet restore tools/Bootnode/Nethermind.Bootnode/Nethermind.Bootnode.csproj -a $arch -p:SaveDiskSpace=true && \
  dotnet publish tools/Bootnode/Nethermind.Bootnode/Nethermind.Bootnode.csproj \
    -c $BUILD_CONFIG \
    -a $arch \
    -o /publish \
    --no-restore \
    --no-self-contained \
    -p:SaveDiskSpace=true \
    -p:SourceRevisionId=$COMMIT_HASH

FROM mcr.microsoft.com/dotnet/aspnet:11.0.0-rc.1-resolute@sha256:bd68c1b5bd48ce7da7ca762f149c3760984f9b9e91a44579d3591e36ac1c7701

ARG COMMIT_HASH=unknown
ARG VERSION=unknown
ARG BUILD_TIMESTAMP=1970-01-01T00:00:00Z

# An ARG is out of scope after a FROM, so the three above must be redeclared in this stage.
# Without that, the values below silently resolve to an empty string. The defaults keep a plain
# `docker build` with no build args self-describing rather than blank.
LABEL org.opencontainers.image.title="Nethermind Bootnode" \
  org.opencontainers.image.description="Standalone discovery bootnode for discv4 and discv5." \
  org.opencontainers.image.vendor="Demerzel Solutions Limited" \
  org.opencontainers.image.licenses="LGPL-3.0-only" \
  org.opencontainers.image.url="https://github.com/NethermindEth/nethermind/tree/master/tools/Bootnode" \
  org.opencontainers.image.documentation="https://github.com/NethermindEth/nethermind/blob/master/tools/Bootnode/README.md" \
  org.opencontainers.image.source="https://github.com/NethermindEth/nethermind" \
  org.opencontainers.image.version="$VERSION" \
  org.opencontainers.image.revision="$COMMIT_HASH" \
  org.opencontainers.image.created="$BUILD_TIMESTAMP"

WORKDIR /nethermind-bootnode

VOLUME /nethermind-bootnode/data

EXPOSE 30303/udp 8546 6060

COPY --from=build /publish .
COPY tools/Bootnode/docker-entrypoint.sh .

RUN chmod +x docker-entrypoint.sh

ENTRYPOINT ["./docker-entrypoint.sh"]
