# The container sidecar of the profiling MCP server, from the self-contained Linux build that a publish puts in its linux-x64 folder:
#   docker build -f sidecar.Dockerfile linux-x64
# The sidecar needs only the native libraries of a self-contained .NET app; BASE may be any glibc-based image.
ARG BASE=mcr.microsoft.com/dotnet/runtime-deps:10.0
FROM ${BASE}
# perf for native_sample. Ubuntu ships it per kernel; any of its builds runs on another kernel. Skipped on a base without apt, where native_sample reports it missing.
RUN if command -v apt-get >/dev/null 2>&1; then \
      apt-get update && \
      { apt-get install -y --no-install-recommends linux-tools-generic || apt-get install -y --no-install-recommends linux-perf; } && \
      rm -rf /var/lib/apt/lists/* && \
      for perf in /usr/lib/linux-tools/*/perf; do if [ -x "$perf" ]; then ln -sf "$perf" /usr/local/bin/perf; fi; done; \
    fi || true
COPY . /profmcp/
ENTRYPOINT ["/profmcp/Nethermind.Tools.ProfilingMcp"]
CMD ["--sidecar-idle"]
