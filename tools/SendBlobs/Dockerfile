# SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:9.0 AS build

ARG BUILD_CONFIG=release
ARG BUILD_TIMESTAMP
ARG COMMIT_HASH
ARG TARGETARCH
ARG TARGETOS

COPY ./src/Nethermind ./src/Nethermind
COPY ./tools/SendBlobs ./tools/SendBlobs
COPY ./Directory.Build.props ./Directory.Build.props
COPY ./Directory.Packages.props ./Directory.Packages.props
COPY ./global.json ./global.json
COPY ./nuget.config ./nuget.config
COPY ./.editorconfig ./.editorconfig
COPY ./tools/Directory.Build.props ./tools/Directory.Build.props

RUN arch=$([ "$TARGETARCH" = "amd64" ] && echo "x64" || echo "$TARGETARCH") && \
  dotnet publish tools/SendBlobs/SendBlobs.csproj -c $BUILD_CONFIG -r $TARGETOS-$arch -o /out --sc true \
  -p:BuildTimestamp=$BUILD_TIMESTAMP -p:Commit=$COMMIT_HASH -p:DebugType=None -p:DebugSymbols=false

FROM --platform=$TARGETPLATFORM ubuntu

WORKDIR /nethermind

COPY --from=build /out ./
RUN apt update && apt install -y ca-certificates

ENTRYPOINT ["./SendBlobs"]
