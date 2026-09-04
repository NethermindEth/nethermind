# SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.400-resolute@sha256:17d5b93701079599eb771cf72f2aaa45a5826d9802f2cfbae565fd2eecf72073 AS build

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

FROM mcr.microsoft.com/dotnet/aspnet:10.0.11-resolute@sha256:e12b240891f34144edd813a11e86649dca6120165adfb5ad0a29bbde6753a975

WORKDIR /nethermind

# RocksDB statically links its own jemalloc; this puts jemalloc under the rest of the native heap
# (runtime, interop, networking) too. Bare soname resolves per-arch (amd64/arm64); the maps grep
# fails the build if ld.so cannot preload it, since a bad soname is otherwise only a warning.
RUN apt-get update && \
  apt-get install -y --no-install-recommends libjemalloc2 && \
  rm -rf /var/lib/apt/lists/* && \
  LD_PRELOAD=libjemalloc.so.2 sh -c 'grep -q jemalloc /proc/self/maps'

ENV LD_PRELOAD=libjemalloc.so.2
# background_thread:true was the keeper lever in the 2026-07 allocator campaign: 3x tighter CV,
# lowest RSS and throttle, latency flat.
ENV MALLOC_CONF=background_thread:true

VOLUME /nethermind/keystore
VOLUME /nethermind/logs
VOLUME /nethermind/nethermind_db

EXPOSE 8545 8551 30303

COPY --from=build /publish .
COPY scripts/entrypoint.sh .

ENTRYPOINT ["./entrypoint.sh"]
