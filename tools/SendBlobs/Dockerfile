# SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:9.0-noble AS build

ARG BUILD_CONFIG=release
ARG BUILD_TIMESTAMP
ARG COMMIT_HASH
ARG TARGETARCH
ARG TARGETOS

COPY ./src/Nethermind ./src/Nethermind
COPY ./tools/SendBlobs ./tools/SendBlobs
COPY ./Directory.*.props .
COPY ./global.json ./global.json
COPY ./nuget.config ./nuget.config
COPY ./tools/Directory.Build.props ./tools/Directory.Build.props

RUN arch=$([ "$TARGETARCH" = "amd64" ] && echo "x64" || echo "$TARGETARCH") && \
  dotnet publish tools/SendBlobs/SendBlobs.csproj -c $BUILD_CONFIG -r $TARGETOS-$arch -o /out --sc true \
  -p:DebugType=None \
  -p:DebugSymbols=false

FROM mcr.microsoft.com/dotnet/runtime-deps:9.0-noble

WORKDIR /nethermind

COPY --from=build /out ./
RUN apt update && apt install -y ca-certificates

ENTRYPOINT ["./SendBlobs"]
