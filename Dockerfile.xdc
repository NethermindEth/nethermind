# XDC Nethermind (nmx) - OS Agnostic Docker Image
# Based on official nethermind/nethermind Dockerfile
# Multi-arch support: linux/amd64, linux/arm64
# Source: https://github.com/AnilChinchawale/nethermind/tree/build/xdc-net9-stable

# SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:9.0-noble AS build

ARG BUILD_CONFIG=release
ARG CI=true
ARG COMMIT_HASH
ARG SOURCE_DATE_EPOCH
ARG TARGETARCH
ARG GIT_BRANCH=build/xdc-net9-stable
ARG GIT_REPO=https://github.com/AnilChinchawale/nethermind.git

WORKDIR /nethermind

# Install git
RUN apt-get update && apt-get install -y --no-install-recommends git && rm -rf /var/lib/apt/lists/*

# Clone XDC Nethermind
RUN git clone -b ${GIT_BRANCH} --depth 1 ${GIT_REPO} .

# Source already cloned from GitHub above — no local COPY needed

RUN arch=$([ "$TARGETARCH" = "amd64" ] && echo "x64" || echo "$TARGETARCH") && \
  rid="linux-${arch}" && \
  cd src/Nethermind/Nethermind.Runner && \
  dotnet restore -r $rid && \
  dotnet publish -c $BUILD_CONFIG -r $rid -o /publish --no-restore --self-contained \
    -p:SourceRevisionId=$COMMIT_HASH -p:PublishSingleFile=true

# A temporary symlink to support the old executable name
# Create symlink for backward compatibility (binary may be nethermind or Nethermind.Runner)
RUN ls -la /publish/nethermind* /publish/Nethermind* 2>/dev/null || true && \
  if [ -f /publish/nethermind ]; then ln -sf nethermind /publish/Nethermind.Runner; \
  elif [ -f /publish/Nethermind.Runner ]; then ln -sf Nethermind.Runner /publish/nethermind; fi

FROM mcr.microsoft.com/dotnet/aspnet:9.0-noble

WORKDIR /nethermind

# Install runtime dependencies
RUN apt-get update && apt-get install -y --no-install-recommends curl jq && rm -rf /var/lib/apt/lists/*

# Create non-root user
RUN useradd -m nethermind 2>/dev/null || true

# Create data directory
RUN mkdir -p /data/nethermind /nethermind/keystore /nethermind/logs /nethermind/nethermind_db && \
    chown -R nethermind:nethermind /data /nethermind

# Declare volumes
VOLUME /nethermind/keystore
VOLUME /nethermind/logs
VOLUME /nethermind/nethermind_db

# Copy binaries
COPY --from=build /publish .

# Fix permissions
RUN chown -R nethermind:nethermind /nethermind

USER nethermind

WORKDIR /data

# Expose ports
# 8557: HTTP RPC
# 8558: WebSocket
# 30305: P2P TCP/UDP
EXPOSE 8557 8558 30305 30305/udp

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=60s --retries=5 \
    CMD curl -sf http://localhost:8557/health > /dev/null || exit 1

# Labels
LABEL org.opencontainers.image.title="XDC Nethermind" \
      org.opencontainers.image.description="XDC Network Nethermind client" \
      org.opencontainers.image.source="https://github.com/AnilChinchawale/nethermind"

ENTRYPOINT ["/nethermind/nethermind"]
