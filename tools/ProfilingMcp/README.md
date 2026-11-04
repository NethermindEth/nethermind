# ProfilingMcp

An [MCP](https://modelcontextprotocol.io) server that lets an AI agent profile .NET processes. By default it uses EventPipe, the runtime's own diagnostics channel, for CPU samples, GC and exception events, and heap snapshots: it needs no profiler or ptrace, works inside containers, and can be repeated on a live process. JetBrains dotTrace (launching apps, exact call counts) and ClrMD dump analysis (field names) cover what EventPipe cannot. Tool results are plain-text tables an agent can read directly.

## Tools

| Tool | What it does |
| --- | --- |
| `list_containers` | Running containers of a Docker engine, and the Docker contexts that reach other engines |
| `list_dotnet_processes` | Running .NET processes with a diagnostics endpoint |
| `runtime_counters` | GC counts, reasons and pauses, and heap size per generation over a window; plus `System.Runtime` counters (allocation rate, thread pool, contention, CPU) when the app has EventSource enabled |
| `cpu_sample` | Sample a running process over EventPipe for a window, with GC and exception events; saves a `.nettrace` and summarizes hotspots, GC pauses and thrown exceptions. Repeatable and safe on live processes |
| `native_sample` | Sample every thread of a Linux process with `perf`, native code included (RocksDB, the runtime and GC, the kernel), with JIT-compiled .NET methods named from the runtime's perf map without a restart; saves `.folded` stacks and summarizes CPU time per thread and the hotspots. Works for processes that are not .NET |
| `trace_start` / `trace_attach` | Launch an app under dotTrace or attach dotTrace to a PID; returns a session id |
| `trace_snapshot` / `trace_collect` / `trace_stop` / `trace_sessions` | Control a session: save a snapshot, start or drop collection, end it, list them |
| `trace_hotspots` | Rank methods in a dotTrace snapshot, EventPipe trace or native stacks by own or total time, with include/exclude regexes |
| `trace_callstacks` | The distinct call stacks reaching a method, ranked by time |
| `trace_compare` | Per-method time deltas between two snapshots or two traces of the same kind |
| `trace_report` | A self-contained HTML page for people: interactive flame graph and sortable hotspot table, with search and wait/pseudo-frame filters |
| `heap_stats` | Per-type counts and sizes of live objects, generations, fragmentation; saved for diffing. A PID is read over EventPipe with one induced full GC; a dump with ClrMD |
| `heap_diff` | Types that grew or shrank between two `heap_stats` snapshots |
| `native_memory` | Split a Linux process's resident memory into the GC heap by generation, the runtime's own heaps and JIT code, thread stacks, glibc malloc, other anonymous mmaps (such as RocksDB's jemalloc) and mapped files. Reads `/proc` and the runtime without pausing the process |
| `heap_retention` | Who references instances of a type, and the shortest chains from GC roots, largest retained bytes first. Chains from a dump also name fields and static fields |
| `alloc_sample` | Allocated bytes per type, allocating method and call stack over a window, from allocation tick events; `include` keeps the allocations with a matching frame |
| `write_dump` | A full-heap dump for offline `heap_stats`/`heap_retention` |

The server's instructions describe the intended workflows (CPU hotspots, leaks, GC pressure) to the agent.

## Requirements

- .NET 10 SDK.
- The dotTrace command-line tools. On first use the server downloads the free [`JetBrains.dotTrace.CommandLineTools`](https://www.nuget.org/packages?q=JetBrains.dotTrace.CommandLineTools) package for the current OS and architecture. Its licence needs no key or subscription and allows redistribution. To use an existing copy, set `DOTTRACE_TOOLS_DIR` to the folder containing `dottrace.exe` (Windows) or `dottrace` (Linux/macOS).
- Analyzing dotTrace snapshots (`trace_hotspots`, `trace_callstacks`, `trace_compare`, `trace_report`) needs Windows, because JetBrains ships `Reporter.exe` only there. On Linux and macOS the server can capture snapshots; copy the `*.dtp*` files to Windows to analyze them. EventPipe traces from `cpu_sample` are analyzed on any OS.
- Timeline profiling on Windows needs the JetBrains ETW Host Service, and its snapshots cannot be analyzed by Reporter. Prefer Sampling.
- `native_memory` needs Linux and ptrace access to the process, which the sidecar has.
- `native_sample` needs Linux and `perf`: the sidecar image includes it, and on a Linux host it comes from `linux-perf` or `linux-tools`. Sampling another process needs `CAP_PERFMON` or root, or a `kernel.perf_event_paranoid` of 1 or less.
- The memory tools work on Windows and Linux. Reading a process over EventPipe needs access to its diagnostics socket (the same user, or root), not ptrace.

Snapshots, heap statistics and dumps go to `%LOCALAPPDATA%\ProfilingMcp` (`~/.local/share/ProfilingMcp` on Linux), or `PROFILING_MCP_HOME` if set. Keep this path short on Windows: the dotTrace console tools fail silently with exit code -1 when started from deeply nested folders.

## Register with Claude Code

Publish once to a folder outside the repository. The publish also puts a Linux build in its `linux-x64` folder, for containers.

```bash
dotnet publish tools/ProfilingMcp/Nethermind.Tools.ProfilingMcp -c Release -o <install folder>
```

Register the executable by its absolute path, for your user so that it works from any folder:

```bash
claude mcp add -s user profiling -- <install folder>/Nethermind.Tools.ProfilingMcp
```

Claude Code connects to MCP servers when a session starts, so start a new session afterwards. Any MCP client that launches a stdio server works the same way.

## Profiling containers

The same server profiles containers; nothing is registered per container. Every tool takes two optional arguments, so the agent picks the target from the request, e.g. "profile my local hoodi node":

- `container`: the container's name or ID, from `list_containers`.
- `dockerHost`: the engine running it, as a Docker context name (`list_containers` lists them) or an address such as `ssh://user@node1`. Omitted, the docker CLI's default engine is used.

A server on the host cannot see processes in a container: EventPipe reaches a process through a socket in the process's own temp folder. So the first call naming a container starts a sidecar container that shares the target's process namespace and runs the same server, built for Linux. Each session then runs its own server in that sidecar with `docker exec` and sends the calls naming the container there. PIDs and file paths in those calls are the container's: pass the same `container` to analyze a trace taken there.

What the target needs, and what it doesn't:

- **Any image.** The sidecar runs its own image, so Alpine, distroless or shell-less targets work.
- **Any user and temp folder.** The sidecar gets `SYS_PTRACE` (the target gets nothing), so it reaches targets running as non-root users, and it follows a `TMPDIR` set in the target's environment.
- **For `native_sample`, a 5.8 or later kernel on the engine.** There the sidecar also gets `PERFMON`, which perf needs to sample another process and which Docker's seccomp profile requires for `perf_event_open`. On older kernels the sidecar starts without it and `native_sample` fails.
- **Diagnostics enabled.** A target with `DOTNET_EnableDiagnostics=0` cannot be profiled; the first call naming it says so.
- **A `linux/amd64` engine.**

Nothing is copied into the target container. No bind mounts are used, so nothing depends on Docker Desktop's file sharing:

- There is one sidecar per container and sidecar image, `profmcp-<container>-<image id>`, shared by all sessions. It stays running and idle between sessions, so later sessions connect in about 3 s, and it ends with its target: recreating or upgrading the target needs nothing more than the next call.
- The first call for a container on an engine gets the sidecar image, once per server version:
  1. a `profiling-mcp:<hash>` image built earlier from this install's Linux build;
  2. else `nethermindeth/profiling-mcp:<commit>`, pulled from Docker Hub, where `<commit>` is the commit the server was built from;
  3. else one built from the install's `linux-x64` folder with `sidecar.Dockerfile`, on .NET's `mcr.microsoft.com/dotnet/runtime-deps:10.0` image, or on the target's image when the engine cannot pull that. Building sends about 70 MB to the engine and installs `perf` from the base image's package repository, so it needs network access; on a base without `apt` the image is built without `perf`.

  Set `PROFILING_MCP_SIDECAR_IMAGE` in the server's environment to `local` to skip the pull, as for a server built with uncommitted changes, or to an image to use instead. Old images and sidecars can be removed with `docker image rm` and `docker rm -f`.
- Traces, reports, heap statistics and dumps go to a `profmcp-home-<container>` volume and are kept across sessions until the volume is removed with `docker volume rm profmcp-home-<container>` (stop the sidecar first). `cpu_sample`, `alloc_sample` and `trace_report` give the `docker cp` command that copies their file to this machine. A dump is first written to the target's temp folder, then moved into the volume; it is as large as the target's memory and needs that much free disk in both places.

Verified against a hoodi node on `nethermindeth/nethermind:master` (.NET 10.0.14), and on a separate Docker engine against targets running as root, as an unprivileged user, and with a custom `TMPDIR`. Things that differ from a local Windows process:

- **No dotTrace.** dotTrace loads its profiler into the target from a path the target cannot see, so `trace_start` and `trace_attach` are refused; use `cpu_sample`. Running the server inside a Linux process's own container, where dotTrace attaches, has a further catch: detaching a Sampling profiler kills the process with SIGSEGV, so the server never detaches on Linux and a process can be attached to once per run.
- **No `System.Runtime` counters.** Nethermind sets `System.Diagnostics.Tracing.EventSource.IsSupported=false`. `runtime_counters` still reports GC activity from the runtime's native events, and `alloc_sample` works.
- **Heap reads over EventPipe are cheap.** On a synced node with 811 MB of live objects (2.2 million), `heap_stats` took 0.9 s and the induced GC paused the node for 390–550 ms; `heap_retention` took under 2 s. A `write_dump` of a 2 GB-RSS node, by comparison, wrote 5 GB and paused it for about 40 s, so keep dumps for when field names are needed. The sidecar analyzes a dump while the node runs, finding the node's runtime files in its container.
- **CPU traces are cheap too.** `cpu_sample` can sample the node any number of times. A 60 s sample of a synced hoodi node wrote a 67 MB trace; analyzing it briefly needs a temporary index about four times that size next to the trace.
- **Native samples see inside native code.** Under `eth_getLogs` load, `native_sample` put 55 % of the node's CPU time inside `librocksdb.so`, which `cpu_sample` can only show as own time of `DbOnTheRocks.GetNativeSlice`. Asking the runtime for its perf map named about 75,000 JIT-compiled methods; the runtime writes the map, about 15 MB, to its temp folder while sampling, and the sidecar deletes it afterwards.
- **Most of the node's memory is native.** `native_memory` on the same node, at 2.6 GB RSS, found 570 MB in the GC heap and 1.77 GB of native memory, almost all of it anonymous mmaps outside glibc's malloc: `librocksdb.so` carries its own jemalloc. RocksDB's own figures in the node's metrics (`nethermind_db_block_cache_size`, `nethermind_db_index_filter_size`, `nethermind_db_memtable_size`) accounted for about 1 GB of it; the rest is jemalloc's retained pages and other native allocations, which no tool here names by code.

## Caveats

- Reading a PID makes the runtime run a full blocking GC, during which it reports every surviving object; expect a pause of roughly 0.5 s per GB of live heap. The server holds the object graph in memory for `heap_retention`: on a heap of 2.2 million objects it peaked at 340 MB, and it grows with the object and reference count.
- The runtime names nested types without their declaring type (`Bucket[]`, `Entry<System.String, System.Int32>[]`), so unrelated nested types of the same name are counted together. `heap_retention` shows which types hold them; a dump gives full names.
- `heap_retention` keeps a visited-object map for its root search; `maxVisited` bounds that map on very large heaps, but not the object graph read before it, which takes memory in proportion to the heap's objects and references.
- Sizes from `heap_stats` are shallow (an object's own size, excluding what it references).
- `native_sample` measures CPU time at 1 ms per sample; blocked threads are not sampled, so it says nothing about waits. Stacks are walked by frame pointers, which JIT-compiled code keeps; native libraries built without them show shortened stacks. Libraries without symbols, such as Nethermind's `librocksdb.so` and the runtime's `libcoreclr.so`, show as one bracketed frame, and names in stripped system libraries such as `libc.so.6` are perf's nearest exported symbol and can be wrong.
- `native_memory` attributes whole mappings: one that is partly a known range is split in proportion to its bytes, and the runtime is read while it runs, so small categories are estimates. It cannot say which code allocated native memory, and allocator RSS includes freed memory the allocator keeps.
- EventPipe samples managed threads only. Time a thread spends in native code, whether working (such as KZG) or blocked, is own time of the managed method that called it, so blocking waits are told apart by method name as with dotTrace.
- dotTrace Sampling times are wall-clock milliseconds summed over all threads, so idle threads dominate a mostly idle process. `trace_hotspots` hides known blocking waits by default (`hideWaits`), but total times of callers still include them. Bracketed rows such as `[Native or optimized code]` are profiler pseudo-frames, and on Linux they also absorb time native threads spend blocked.
- `heap_stats` of a dump counts every object on the heap, including unreachable ones the GC has not collected yet. Reading a PID counts only objects that survived the induced GC.
