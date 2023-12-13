# SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:8.0-jammy AS build

ARG BUILD_CONFIG=release
ARG BUILD_TIMESTAMP
ARG CI
ARG COMMIT_HASH
ARG TARGETARCH

COPY .git .git
COPY src/Nethermind src/Nethermind

RUN arch=$([ "$TARGETARCH" = "amd64" ] && echo "x64" || echo "$TARGETARCH") && \
    dotnet publish src/Nethermind/Nethermind.Runner -c $BUILD_CONFIG -a $arch -o /publish --sc false \
      -p:BuildTimestamp=$BUILD_TIMESTAMP -p:Commit=$COMMIT_HASH

# A temporary symlink to support the old executable name
RUN ln -s -r /publish/nethermind /publish/Nethermind.Runner

FROM --platform=$TARGETPLATFORM mcr.microsoft.com/dotnet/aspnet:8.0-jammy

WORKDIR /nethermind

VOLUME /nethermind/keystore
VOLUME /nethermind/logs
VOLUME /nethermind/nethermind_db

EXPOSE 8545 8551 30303

COPY --from=build /publish .

RUN apt-get update && apt-get -y install libsnappy-dev && \
  rm -rf /var/lib/apt/lists/*

ENTRYPOINT ["./nethermind"]
