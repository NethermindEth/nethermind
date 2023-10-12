# SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:8.0-jammy AS build

ARG BUILD_CONFIG=release
ARG BUILD_TIMESTAMP
ARG COMMIT_HASH
ARG TARGETARCH
ARG TARGETOS

COPY . .

RUN arch=$([ "$TARGETARCH" = "amd64" ] && echo "x64" || echo $TARGETARCH) && \
    dotnet publish src/Nethermind/Nethermind.Runner -c $BUILD_CONFIG -r $TARGETOS-$arch -o pub --sc false \
      -p:BuildTimestamp=$BUILD_TIMESTAMP -p:Commit=$COMMIT_HASH -p:Deterministic=true

# A temporary symlink to support the old executable name
RUN ln -s -r pub/nethermind pub/Nethermind.Runner

FROM --platform=$TARGETPLATFORM mcr.microsoft.com/dotnet/aspnet:8.0-jammy

WORKDIR /nethermind

EXPOSE 8545 8551 30303

VOLUME /nethermind/keystore
VOLUME /nethermind/logs
VOLUME /nethermind/nethermind_db

RUN apt-get update && apt-get -y install libsnappy-dev

COPY --from=build /pub .

ENTRYPOINT ["./nethermind"]
