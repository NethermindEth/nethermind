# syntax=docker/dockerfile:1.26@sha256:ecfaec9ed6d810b56388c508f4121597bfbba70d41a6dfeee4d8cad5f295fc32
# SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.403-resolute@sha256:2107242a0f56885d09f8a536ac300f6cb1a2b3f6d40605e5ff81bbd83ad91e3f AS build

ARG BUILD_CONFIG=release
ARG CI=true
ARG COMMIT_HASH
ARG SOURCE_DATE_EPOCH
ARG TARGETARCH

WORKDIR /nethermind

COPY global.json nuget.config Directory.Build.props Directory.Build.targets Directory.Packages.props ./
COPY --parents src/Nethermind/**/*.csproj src/Nethermind/Directory.Build.props src/Nethermind/Directory.Build.targets src/Nethermind/Nethermind.Runner/packages.lock.json ./
RUN cd src/Nethermind/Nethermind.Runner && dotnet restore --locked-mode

# Benchmark only: replace the restored Int256 1.12.0 assembly with the same library built from
# NethermindEth/int256 6ff523e (v1.12.0 plus the MULMOD/ADDMOD changes in UInt256.divmod.cs).
ARG INT256_COMMIT=6ff523ecb4be6c2f4671e79b4ce97884a27231bf
RUN git clone https://github.com/NethermindEth/int256 /int256 && \
  cd /int256 && git checkout --detach $INT256_COMMIT && \
  git diff --quiet v1.12.0 $INT256_COMMIT -- src/Nethermind.Int256 ':!src/Nethermind.Int256/UInt256.divmod.cs' && \
  dotnet build src/Nethermind.Int256/Nethermind.Int256.csproj -c Release -p:TreatWarningsAsErrors=false && \
  target=/root/.nuget/packages/nethermind.numerics.int256/1.12.0/impl/std/net10.0/Nethermind.Int256.dll && \
  test -f $target && \
  cp artifacts/bin/Nethermind.Int256/release/Nethermind.Int256.dll $target && \
  sha256sum $target

COPY src/Nethermind src/Nethermind

RUN arch=$([ "$TARGETARCH" = "amd64" ] && echo "x64" || echo "$TARGETARCH") && \
  cd src/Nethermind/Nethermind.Runner && \
  dotnet publish -c $BUILD_CONFIG -a $arch -o /publish --no-restore --no-self-contained \
    -p:SourceRevisionId=$COMMIT_HASH

# A temporary symlink to support the old executable name
RUN ln -sr /publish/nethermind /publish/Nethermind.Runner

FROM mcr.microsoft.com/dotnet/aspnet:10.0.14-resolute@sha256:cb0e8cadcf227143d90cf2a861106f2de9b2d404e7e9806d5817bbb5400db97d

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

EXPOSE 8545 8551 30303 30303/udp

COPY --from=build /publish .
COPY scripts/entrypoint.sh .

ENTRYPOINT ["./entrypoint.sh"]
