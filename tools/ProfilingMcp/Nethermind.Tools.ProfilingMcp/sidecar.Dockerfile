# The container sidecar of the profiling MCP server, from the self-contained Linux build that a publish puts in its linux-x64 folder:
#   docker build -f sidecar.Dockerfile linux-x64
# The sidecar needs only the native libraries of a self-contained .NET app; BASE may be any glibc-based image.
ARG BASE=mcr.microsoft.com/dotnet/runtime-deps:10.0
FROM ${BASE}
COPY . /profmcp/
ENTRYPOINT ["/profmcp/Nethermind.Tools.ProfilingMcp"]
CMD ["--sidecar-idle"]
