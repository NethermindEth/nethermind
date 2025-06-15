# SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:9.0-noble AS build

ARG BUILD_CONFIG=release
ARG BUILD_TIMESTAMP
ARG CI
ARG COMMIT_HASH
ARG TARGETARCH

COPY src/Nethermind src/Nethermind
COPY Directory.*.props .
COPY nuget.config .

RUN arch=$([ "$TARGETARCH" = "amd64" ] && echo "x64" || echo "$TARGETARCH") && \
  dotnet publish src/Nethermind/Nethermind.Runner -c $BUILD_CONFIG -a $arch -o /publish --sc true \
  -p:BuildTimestamp=$BUILD_TIMESTAMP -p:Commit=$COMMIT_HASH

RUN git clone https://github.com/rubo/satori-bin.git && \
  cp -rf ./satori-bin/linux-x64/* /publish

FROM ubuntu:noble

WORKDIR /nethermind

VOLUME /nethermind/keystore
VOLUME /nethermind/logs
VOLUME /nethermind/nethermind_db

EXPOSE 8545 8551 30303

COPY --from=build /publish .

ENV DOTNET_ReadyToRun=0
ENV DOTNET_gcGen0=0
ENV DOTNET_gcGen2Target=0x200
ENV DOTNET_gcPace=0

ENTRYPOINT ["./nethermind"]
