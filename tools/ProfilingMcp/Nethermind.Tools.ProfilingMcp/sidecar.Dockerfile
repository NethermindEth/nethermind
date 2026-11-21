# The container sidecar of the profiling MCP server, from a self-contained Linux build that a publish puts in its linux-x64 or linux-arm64 folder:
#   docker build -f sidecar.Dockerfile linux-x64
# The sidecar needs only the native libraries of a self-contained .NET app; BASE may be any glibc-based image.
ARG BASE=mcr.microsoft.com/dotnet/runtime-deps:11.0
FROM ${BASE}
# perf for native_sample. Debian and Ubuntu 26.04 ship it as linux-perf; earlier Ubuntu ships it per kernel in linux-tools, and any of
# its builds runs on another kernel. Ubuntu 26.04 still has linux-tools-generic, without perf, so linux-perf is tried first.
# A failed install fails the build. On a base without apt it is skipped, and native_sample reports perf missing.
RUN if command -v apt-get >/dev/null 2>&1; then \
      apt-get update && \
      { apt-get install -y --no-install-recommends linux-perf || apt-get install -y --no-install-recommends linux-tools-generic; } && \
      rm -rf /var/lib/apt/lists/* && \
      perf=$(for p in /usr/lib/linux-tools/*/perf /usr/bin/perf; do [ -x "$p" ] && echo "$p" && break; done) && \
      ln -sf "$perf" /usr/local/bin/perf && \
      /usr/local/bin/perf --version; \
    fi
COPY . /profmcp/
ENTRYPOINT ["/profmcp/Nethermind.Tools.ProfilingMcp"]
CMD ["--sidecar-idle"]
