# SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:9.0-noble AS build

ARG BUILD_CONFIG=release
ARG BUILD_TIMESTAMP
ARG COMMIT_HASH
ARG TARGETARCH
ARG TARGETOS

COPY . .

RUN arch=$([ "$TARGETARCH" = "amd64" ] && echo "x64" || echo "$TARGETARCH") && \
    dotnet publish tools/SendBlobs -c $BUILD_CONFIG -r $TARGETOS-$arch -o out --sc true \
      -p:BuildTimestamp=$BUILD_TIMESTAMP -p:Commit=$COMMIT_HASH

FROM --platform=$TARGETPLATFORM ubuntu

WORKDIR /nethermind

COPY --from=build /out/SendBlobs .

RUN apt update && apt-get install -y ca-certificates
ENTRYPOINT ["./SendBlobs"]
