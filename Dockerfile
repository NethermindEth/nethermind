# SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:7.0-jammy AS build

ARG TARGETPLATFORM
ARG TARGETOS
ARG TARGETARCH
ARG BUILDPLATFORM
ARG BUILD_CONFIG=release
ARG BUILD_TIMESTAMP
ARG COMMIT_HASH

COPY . .

RUN if [ "$TARGETARCH" = "amd64" ]; \
    then dotnet publish src/Nethermind/Nethermind.Runner -c $BUILD_CONFIG -r $TARGETOS-x64 -o out --sc false \
      -p:BuildTimestamp=$BUILD_TIMESTAMP -p:Commit=$COMMIT_HASH -p:Deterministic=true ; \
    else dotnet publish src/Nethermind/Nethermind.Runner -c $BUILD_CONFIG -r $TARGETOS-$TARGETARCH -o out --sc false \
      -p:BuildTimestamp=$BUILD_TIMESTAMP -p:Commit=$COMMIT_HASH -p:Deterministic=true ; \
    fi

# A temporary symlink to support the old executable name
RUN ln -s -r out/nethermind out/Nethermind.Runner

FROM --platform=$TARGETPLATFORM mcr.microsoft.com/dotnet/aspnet:7.0-jammy

ARG TARGETPLATFORM
ARG TARGETOS
ARG TARGETARCH
ARG BUILDPLATFORM

RUN apt-get update && apt-get -y install libsnappy-dev

WORKDIR /nethermind

COPY --from=build /out .

LABEL git_commit=$COMMIT_HASH

EXPOSE 8545 8551 30303

VOLUME /nethermind/nethermind_db
VOLUME /nethermind/logs
VOLUME /nethermind/keystore

ENTRYPOINT ["./nethermind"]
