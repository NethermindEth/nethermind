# The container sidecar of the profiling MCP server, from a self-contained Linux build that a publish puts in its linux-x64 or linux-arm64 folder:
#   docker build -f sidecar.Dockerfile linux-x64
# The sidecar needs only the native libraries of a self-contained .NET app; BASE may be any glibc-based image.
ARG BASE=mcr.microsoft.com/dotnet/runtime-deps:10.0
FROM ${BASE}
# perf for native_sample. Ubuntu ships it per kernel, and any of its builds runs on another kernel; Debian ships it as linux-perf.
# A failed install fails the build. On a base without apt it is skipped, and native_sample reports perf missing.
RUN if command -v apt-get >/dev/null 2>&1; then \
      apt-get update && \
      { apt-get install -y --no-install-recommends linux-tools-generic || apt-get install -y --no-install-recommends linux-perf; } && \
      rm -rf /var/lib/apt/lists/* && \
      perf=$(for p in /usr/lib/linux-tools/*/perf /usr/bin/perf; do [ -x "$p" ] && echo "$p" && break; done) && \
      ln -sf "$perf" /usr/local/bin/perf && \
      /usr/local/bin/perf --version; \
    fi
COPY . /profmcp/
ENTRYPOINT ["/profmcp/Nethermind.Tools.ProfilingMcp"]
CMD ["--sidecar-idle"]
