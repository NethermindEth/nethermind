# SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.302-resolute@sha256:45401dde65ffc706a65841120ffdf827805eefe16852d6de1086a876c421de2e AS build

ARG BUILD_CONFIG=release
ARG CI=true
ARG COMMIT_HASH
ARG SOURCE_DATE_EPOCH
ARG TARGETARCH

WORKDIR /nethermind

COPY src/Nethermind src/Nethermind
COPY Directory.*.props .
COPY Directory.Build.targets .
COPY global.json .
COPY nuget.config .

RUN arch=$([ "$TARGETARCH" = "amd64" ] && echo "x64" || echo "$TARGETARCH") && \
  cd src/Nethermind/Nethermind.Runner && \
  dotnet restore --locked-mode && \
  dotnet publish -c $BUILD_CONFIG -a $arch -o /publish --no-restore --no-self-contained \
    -p:SourceRevisionId=$COMMIT_HASH

# A temporary symlink to support the old executable name
RUN ln -sr /publish/nethermind /publish/Nethermind.Runner

FROM mcr.microsoft.com/dotnet/aspnet:10.0.10-resolute@sha256:dae546296490fa23d67a7d26d901864866c235e7ea59966cdb8f0e680ed25ad9

WORKDIR /nethermind

# Route native allocations (RocksDB block cache, memtables, iterators, decode buffers) through
# tcmalloc instead of glibc malloc. Measured on mainnet fusaka replay (50k blocks, n=5): -1.9%
# newPayload avg, -3% p99, -3.4% CPU and -9% RSS vs the glibc default. Bare soname keeps the
# preload architecture-independent (resolved per-arch by the dynamic linker).
RUN apt-get update && apt-get install -y --no-install-recommends libtcmalloc-minimal4 && rm -rf /var/lib/apt/lists/*
ENV LD_PRELOAD=libtcmalloc_minimal.so.4

VOLUME /nethermind/keystore
VOLUME /nethermind/logs
VOLUME /nethermind/nethermind_db

EXPOSE 8545 8551 30303

COPY --from=build /publish .
COPY scripts/entrypoint.sh .

ENTRYPOINT ["./entrypoint.sh"]
