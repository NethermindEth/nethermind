# syntax=docker/dockerfile:1.7-labs@sha256:b99fecfe00268a8b556fad7d9c37ee25d716ae08a5d7320e6d51c4dd83246894
# SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.400-resolute@sha256:e9d9e903cc6eb4049f3c07d8a86ffdccdd0511a91c3d459c4c4e68d1dab91adf AS build

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

FROM mcr.microsoft.com/dotnet/aspnet:10.0.11-resolute@sha256:655461545edac45015401bee7e52be93373caf15ceddeadd07f0dfe3e0aa9f19

WORKDIR /nethermind

VOLUME /nethermind/keystore
VOLUME /nethermind/logs
VOLUME /nethermind/nethermind_db

EXPOSE 8545 8551 30303

COPY --from=build /publish .
COPY scripts/entrypoint.sh .

ENTRYPOINT ["./entrypoint.sh"]
