# SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:7.0-jammy AS build

ARG BUILD_CONFIG=release
ARG BUILD_TIMESTAMP
ARG COMMIT_HASH
ARG TARGETARCH
ARG TARGETOS

COPY . .

RUN arch=$([ "$TARGETARCH" = "amd64" ] && echo "x64" || echo "$TARGETARCH") && \
    dotnet publish tools/SendBlobs -c $BUILD_CONFIG -r $TARGETOS-$arch -o pub --sc true \
      -p:BuildTimestamp=$BUILD_TIMESTAMP -p:Commit=$COMMIT_HASH

FROM --platform=$TARGETPLATFORM mcr.microsoft.com/dotnet/runtime:7.0

WORKDIR /nethermind

RUN apt-get update && apt-get -y install libsnappy-dev

COPY --from=build /out/SendBlobs .

ENTRYPOINT ["./SendBlobs"]
