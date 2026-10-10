# syntax=docker/dockerfile:1.26@sha256:ecfaec9ed6d810b56388c508f4121597bfbba70d41a6dfeee4d8cad5f295fc32
# SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:11.0.100-rc.1-resolute@sha256:ab11199f8a0cded1d667111a0d4f0906567c3a17348a7181f9781d12cc5eb6f9 AS build

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

FROM mcr.microsoft.com/dotnet/aspnet:11.0.0-rc.1-resolute@sha256:bd68c1b5bd48ce7da7ca762f149c3760984f9b9e91a44579d3591e36ac1c7701

ARG COMMIT_HASH=unknown
ARG VERSION=unknown
ARG BUILD_TIMESTAMP=1970-01-01T00:00:00Z

# An ARG is out of scope after a FROM, so the three above must be redeclared in this stage.
# Without that, the values below silently resolve to an empty string. The defaults keep a plain
# `docker build` with no build args self-describing rather than blank.
LABEL org.opencontainers.image.title="Nethermind" \
  org.opencontainers.image.description="A robust execution client for Ethereum node operators." \
  org.opencontainers.image.vendor="Demerzel Solutions Limited" \
  org.opencontainers.image.licenses="LGPL-3.0-only" \
  org.opencontainers.image.url="https://nethermind.io/nethermind-client" \
  org.opencontainers.image.documentation="https://docs.nethermind.io" \
  org.opencontainers.image.source="https://github.com/NethermindEth/nethermind" \
  org.opencontainers.image.version="$VERSION" \
  org.opencontainers.image.revision="$COMMIT_HASH" \
  org.opencontainers.image.created="$BUILD_TIMESTAMP"

WORKDIR /nethermind

VOLUME /nethermind/keystore
VOLUME /nethermind/logs
VOLUME /nethermind/nethermind_db

EXPOSE 8545 8551 30303

COPY --from=build /publish .
COPY scripts/entrypoint.sh .

ENTRYPOINT ["./entrypoint.sh"]
