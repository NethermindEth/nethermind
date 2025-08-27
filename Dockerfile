# SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:8.0-noble AS build

ARG BUILD_CONFIG=release
ARG BUILD_TIMESTAMP
ARG CI
ARG COMMIT_HASH
ARG TARGETARCH

COPY src/Nethermind src/Nethermind

RUN arch=$([ "$TARGETARCH" = "amd64" ] && echo "x64" || echo "$TARGETARCH") && \
    dotnet publish src/Nethermind/Nethermind.Runner -c $BUILD_CONFIG -a $arch -o /publish --sc false \
      -p:BuildTimestamp=$BUILD_TIMESTAMP -p:Commit=$COMMIT_HASH

# A temporary symlink to support the old executable name
RUN ln -s -r /publish/nethermind /publish/Nethermind.Runner

FROM --platform=$TARGETPLATFORM mcr.microsoft.com/dotnet/aspnet:8.0-noble

WORKDIR /nethermind

VOLUME /nethermind/keystore
VOLUME /nethermind/logs
VOLUME /nethermind/nethermind_db

EXPOSE 8545 8551 30303

COPY --from=build /publish .

COPY --from=pyroscope/pyroscope-dotnet:0.10.0-glibc /Pyroscope.Profiler.Native.so ./Pyroscope.Profiler.Native.so
COPY --from=pyroscope/pyroscope-dotnet:0.10.0-glibc /Pyroscope.Linux.ApiWrapper.x64.so ./Pyroscope.Linux.ApiWrapper.x64.so

ENV PYROSCOPE_APPLICATION_NAME=nethermind \
    PYROSCOPE_SERVER_ADDRESS=http://localhost:4040 \
    PYROSCOPE_PROFILING_ENABLED=1 \
    CORECLR_ENABLE_PROFILING=1 \
    CORECLR_PROFILER={BD1A650D-AC5D-4896-B64F-D6FA25D6B26A} \
    CORECLR_PROFILER_PATH=/nethermind/Pyroscope.Profiler.Native.so \
    LD_PRELOAD=/nethermind/Pyroscope.Linux.ApiWrapper.x64.so \
    DOTNET_EnableDiagnostics=1 \
    DOTNET_EnableDiagnostics_IPC=0 \
    DOTNET_EnableDiagnostics_Debugger=0 \
    DOTNET_EnableDiagnostics_Profiler=1

ENTRYPOINT ["./nethermind"]
