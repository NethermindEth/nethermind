# SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

# ----------------------------
# Build Nethermind
# ----------------------------
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
    dotnet publish src/Nethermind/Nethermind.Runner -c $BUILD_CONFIG -a $arch -o /publish --sc false \
      -p:BuildTimestamp=$BUILD_TIMESTAMP -p:Commit=$COMMIT_HASH

# A temporary symlink to support the old executable name
RUN ln -s -r /publish/nethermind /publish/Nethermind.Runner

# ----------------------------
# Final runtime image
# ----------------------------
FROM mcr.microsoft.com/dotnet/aspnet:9.0-noble

WORKDIR /nethermind

VOLUME /nethermind/keystore
VOLUME /nethermind/logs
VOLUME /nethermind/nethermind_db

EXPOSE 8545 8551 30303

# --- Copy Nethermind build
COPY --from=build /publish .

# --- Copy Pyroscope .NET agent binaries from official image
# pick the latest matching glibc x86_64 build (0.12.0 here, adjust as needed)
COPY --from=pyroscope/pyroscope-dotnet:0.12.0-glibc \
     /Pyroscope.Profiler.Native.so /opt/pyroscope/Pyroscope.Profiler.Native.so
COPY --from=pyroscope/pyroscope-dotnet:0.12.0-glibc \
     /Pyroscope.Linux.ApiWrapper.x64.so /opt/pyroscope/Pyroscope.Linux.ApiWrapper.x64.so

# --- Environment vars to enable Pyroscope CLR profiler
ENV CORECLR_ENABLE_PROFILING=1 \
    CORECLR_PROFILER={BD1A650D-AC5D-4896-B64F-D6FA25D6B26A} \
    CORECLR_PROFILER_PATH=/opt/pyroscope/Pyroscope.Profiler.Native.so \
    LD_PRELOAD=/opt/pyroscope/Pyroscope.Linux.ApiWrapper.x64.so \
    PYROSCOPE_PROFILING_ENABLED=1 \
    PYROSCOPE_APPLICATION_NAME=nethermind \
    PYROSCOPE_SERVER_ADDRESS=http://pyroscope:4040 \
    PYROSCOPE_LOG_LEVEL=debug

ENTRYPOINT ["./nethermind"]
