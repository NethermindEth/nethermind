# syntax=docker/dockerfile:1.7-labs
# SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.301-resolute@sha256:fe81f048c2ff6cdbcc16ad4c1690c5a4f383edab8fdabdf02b3b782591b656c5 AS build

ARG BUILD_CONFIG=release
ARG CI=true
ARG COMMIT_HASH
ARG SOURCE_DATE_EPOCH
ARG TARGETARCH

WORKDIR /nethermind

COPY global.json nuget.config Directory.Build.props Directory.Build.targets Directory.Packages.props ./
COPY --parents src/Nethermind/**/*.csproj src/Nethermind/Directory.Build.props src/Nethermind/Directory.Build.targets src/Nethermind/Nethermind.Runner/packages.lock.json ./
RUN cd src/Nethermind/Nethermind.Runner && dotnet restore --locked-mode

COPY src/Nethermind src/Nethermind

RUN arch=$([ "$TARGETARCH" = "amd64" ] && echo "x64" || echo "$TARGETARCH") && \
  cd src/Nethermind/Nethermind.Runner && \
  dotnet publish -c $BUILD_CONFIG -a $arch -o /publish --no-restore --no-self-contained \
    -p:SourceRevisionId=$COMMIT_HASH

# A temporary symlink to support the old executable name
RUN ln -sr /publish/nethermind /publish/Nethermind.Runner

FROM mcr.microsoft.com/dotnet/aspnet:10.0.9-resolute@sha256:3bb57de5b6c3f5e2f349e9a65f142e4d0fd33c078491956cd9e3e07f4c660c6c

WORKDIR /nethermind

VOLUME /nethermind/keystore
VOLUME /nethermind/logs
VOLUME /nethermind/nethermind_db

EXPOSE 8545 8551 30303

COPY --from=build /publish .
COPY scripts/entrypoint.sh .

ENTRYPOINT ["./entrypoint.sh"]
