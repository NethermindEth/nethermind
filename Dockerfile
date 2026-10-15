# syntax=docker/dockerfile:1.26@sha256:ecfaec9ed6d810b56388c508f4121597bfbba70d41a6dfeee4d8cad5f295fc32
# SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

# The native Patricia bulk set (Nethermind.Trie.Native) is a Rust cdylib that dotnet publish does not build.
# It is cross-compiled here on the build platform and copied to where Nethermind.Trie.Native.csproj looks for it.
FROM --platform=$BUILDPLATFORM rust:1.90-bookworm@sha256:3914072ca0c3b8aad871db9169a651ccfce30cf58303e5d6f2db16d1d8a7e58f AS native-build

ARG TARGETARCH

WORKDIR /rust

COPY src/Nethermind/Nethermind.Trie.Native/rust .

RUN case "$TARGETARCH" in \
    amd64) target=x86_64-unknown-linux-gnu ;; \
    arm64) target=aarch64-unknown-linux-gnu && \
      apt-get update && apt-get install -y --no-install-recommends gcc-aarch64-linux-gnu libc6-dev-arm64-cross && \
      rustup target add $target && \
      export CARGO_TARGET_AARCH64_UNKNOWN_LINUX_GNU_LINKER=aarch64-linux-gnu-gcc CC_aarch64_unknown_linux_gnu=aarch64-linux-gnu-gcc ;; \
    *) echo "Unsupported TARGETARCH: $TARGETARCH" && exit 1 ;; \
  esac && \
  cargo build --release --locked --target $target && \
  mkdir /out && cp target/$target/release/libnethermind_patricia.so /out/

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.402-resolute@sha256:6924c5c6564690089a67509f5a6ae85d7afa28d7f0f6466ec1b8a5439764ca92 AS build

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
COPY --from=native-build /out/libnethermind_patricia.so src/Nethermind/Nethermind.Trie.Native/rust/target/release/

RUN arch=$([ "$TARGETARCH" = "amd64" ] && echo "x64" || echo "$TARGETARCH") && \
  cd src/Nethermind/Nethermind.Runner && \
  dotnet publish -c $BUILD_CONFIG -a $arch -o /publish --no-restore --no-self-contained \
    -p:SourceRevisionId=$COMMIT_HASH

# A temporary symlink to support the old executable name
RUN ln -sr /publish/nethermind /publish/Nethermind.Runner

FROM mcr.microsoft.com/dotnet/aspnet:10.0.13-resolute@sha256:86ddb370c95d79ec988f94e7d66193b20d02a1df859aa3f5e56451141528f053

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
