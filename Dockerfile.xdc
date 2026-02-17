# XDC Nethermind (nmx) - OS Agnostic Docker Image
# Based on official nethermind/nethermind Dockerfile
# Multi-arch support: linux/amd64, linux/arm64
# Source: https://github.com/AnilChinchawale/nethermind/tree/build/xdc-net9-stable

# SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS build

ARG BUILD_CONFIG=release
ARG CI=true
ARG COMMIT_HASH
ARG SOURCE_DATE_EPOCH
ARG TARGETARCH
ARG GIT_BRANCH=build/xdc-net9-stable
ARG GIT_REPO=https://github.com/AnilChinchawale/nethermind.git

WORKDIR /nethermind

# Install git
RUN apk add --no-cache git

# Clone XDC Nethermind
RUN git clone -b ${GIT_BRANCH} --depth 1 ${GIT_REPO} .

COPY src/Nethermind src/Nethermind
COPY Directory.*.props .
COPY global.json .
COPY nuget.config .

RUN arch=$([ "$TARGETARCH" = "amd64" ] && echo "x64" || echo "$TARGETARCH") && \
  cd src/Nethermind/Nethermind.Runner && \
  dotnet restore --locked-mode && \
  dotnet publish -c $BUILD_CONFIG -a $arch -o /publish --no-restore --no-self-contained \
    -p:SourceRevisionId=$COMMIT_HASH

# A temporary symlink to support the old executable name
RUN ln -sr /publish/nethermind /publish/Nethermind.Runner

FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine

WORKDIR /nethermind

# Install runtime dependencies
RUN apk add --no-cache curl jq

# Create non-root user
RUN adduser -D -u 1000 nethermind

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

ENTRYPOINT ["./nethermind"]
