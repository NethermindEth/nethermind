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

FROM mcr.microsoft.com/dotnet/sdk:10.0.302-resolute@sha256:45401dde65ffc706a65841120ffdf827805eefe16852d6de1086a876c421de2e AS rpmalloc-build
RUN apt-get update && apt-get install -y --no-install-recommends gcc libc6-dev git ca-certificates && rm -rf /var/lib/apt/lists/*
RUN git clone --depth 1 https://github.com/mjansson/rpmalloc /rpmalloc
RUN gcc -shared -fPIC -O2 -DNDEBUG -DENABLE_PRELOAD=1 -DENABLE_OVERRIDE=1 -I/rpmalloc/rpmalloc \
    /rpmalloc/rpmalloc/rpmalloc.c -o /librpmallocwrap.so -lpthread -ldl \
 && nm -D /librpmallocwrap.so | grep -qE ' T (malloc|rpmalloc)$' && echo "rpmalloc override symbols present"

FROM mcr.microsoft.com/dotnet/aspnet:10.0.10-resolute@sha256:dae546296490fa23d67a7d26d901864866c235e7ea59966cdb8f0e680ed25ad9

WORKDIR /nethermind

ENV LD_PRELOAD=/nethermind/librpmallocwrap.so

VOLUME /nethermind/keystore
VOLUME /nethermind/logs
VOLUME /nethermind/nethermind_db

EXPOSE 8545 8551 30303

COPY --from=build /publish .
COPY scripts/entrypoint.sh .
COPY --from=rpmalloc-build /librpmallocwrap.so .

ENTRYPOINT ["./entrypoint.sh"]
