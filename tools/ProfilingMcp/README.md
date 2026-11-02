# ProfilingMcp

An [MCP](https://modelcontextprotocol.io) server that lets an AI agent profile .NET processes: CPU with JetBrains dotTrace and memory with the .NET diagnostics libraries (ClrMD and EventPipe). Tool results are plain-text tables an agent can read directly.

## Tools

| Tool | What it does |
| --- | --- |
| `list_dotnet_processes` | Running .NET processes with a diagnostics endpoint |
| `runtime_counters` | GC counts, reasons and pauses, and heap size per generation over a window; plus `System.Runtime` counters (allocation rate, thread pool, contention, CPU) when the app has EventSource enabled |
| `trace_start` / `trace_attach` | Launch an app under dotTrace or attach to a PID; returns a session id |
| `trace_snapshot` / `trace_collect` / `trace_stop` / `trace_sessions` | Control a session: save a snapshot, start or drop collection, end it, list them |
| `trace_hotspots` | Rank methods in a snapshot by own or total time, with include/exclude regexes |
| `trace_callstacks` | The distinct call stacks reaching a method, ranked by time |
| `trace_compare` | Per-method time deltas between two snapshots |
| `trace_report` | A self-contained HTML page for people: interactive flame graph and sortable hotspot table, with search and wait/pseudo-frame filters |
| `heap_stats` | Per-type counts and sizes, generations, fragmentation; saved for diffing |
| `heap_diff` | Types that grew or shrank between two `heap_stats` snapshots |
| `heap_retention` | Who references instances of a type, and the shortest chains from GC roots or static fields |
| `alloc_sample` | Allocated bytes per type over a window, from allocation tick events |
| `write_dump` | A full-heap dump for offline `heap_stats`/`heap_retention` |

The server's instructions describe the intended workflows (CPU hotspots, leaks, GC pressure) to the agent.

## Requirements

- .NET 10 SDK.
- The dotTrace command-line tools. On first use the server downloads the free [`JetBrains.dotTrace.CommandLineTools`](https://www.nuget.org/packages?q=JetBrains.dotTrace.CommandLineTools) package for the current OS and architecture. Its licence needs no key or subscription and allows redistribution. To use an existing copy, set `DOTTRACE_TOOLS_DIR` to the folder containing `dottrace.exe` (Windows) or `dottrace` (Linux/macOS).
- Analyzing dotTrace snapshots (`trace_hotspots`, `trace_callstacks`, `trace_compare`, `trace_report`) needs Windows, because JetBrains ships `Reporter.exe` only there. On Linux and macOS the server can capture snapshots; copy the `*.dtp*` files to Windows to analyze them.
- Timeline profiling on Windows needs the JetBrains ETW Host Service, and its snapshots cannot be analyzed by Reporter. Prefer Sampling.
- The memory tools work on Windows and Linux. On Linux, attaching to another process needs ptrace permission (same user and `kernel.yama.ptrace_scope` ≤ 1, or `CAP_SYS_PTRACE`).

Snapshots, heap statistics and dumps go to `%LOCALAPPDATA%\ProfilingMcp` (`~/.local/share/ProfilingMcp` on Linux), or `PROFILING_MCP_HOME` if set. Keep this path short on Windows: the dotTrace console tools fail silently with exit code -1 when started from deeply nested folders.

## Register with Claude Code

Build once, then register the executable:

```bash
dotnet publish tools/ProfilingMcp/Nethermind.Tools.ProfilingMcp -c Release -o tools/ProfilingMcp/publish
```

```bash
claude mcp add profiling -- tools/ProfilingMcp/publish/Nethermind.Tools.ProfilingMcp
```

Any MCP client that launches a stdio server works the same way.

## Profiling Nethermind in Docker

The server runs inside the node's container, driven over `docker exec`:

```bash
dotnet publish tools/ProfilingMcp/Nethermind.Tools.ProfilingMcp -c Release -r linux-x64 --self-contained -o publish-linux
```

```bash
docker cp publish-linux <container>:/tmp/profmcp
```

```bash
claude mcp add profiling-docker -- docker exec -i -e PROFILING_MCP_HOME=/tmp/profmcp-home <container> /tmp/profmcp/Nethermind.Tools.ProfilingMcp
```

Things that differ from a local Windows process (verified against a hoodi node on `nethermindeth/nethermind:master`, .NET 10.0.14, dotTrace 2026.2.3.1):

- **Detaching dotTrace crashes the node.** Detaching a Sampling profiler from an attached process on Linux kills it with SIGSEGV. This applies to dotTrace's own `--timeout` and to `disconnect`. The server therefore never detaches on Linux: `trace_stop` stops collection and kills the profiler, leaving it idle inside the node. A node can then not be attached to again until it restarts. `timeoutSeconds` is rejected for Linux attach.
- **No `System.Runtime` counters.** Nethermind sets `System.Diagnostics.Tracing.EventSource.IsSupported=false`. `runtime_counters` still reports GC activity from the runtime's native events, and `alloc_sample` works.
- **No live heap reads.** Containers usually lack `CAP_SYS_PTRACE`, so `heap_stats`/`heap_retention` on a PID fail. `write_dump` works because the runtime writes the dump itself. A 2 GB-RSS node produced a 5 GB dump and was paused for about 40 s.
- **Snapshot analysis happens on Windows.** Copy the snapshot directory out with `docker cp` and run `trace_hotspots` there.

## Caveats

- `heap_stats` and `heap_retention` read the target's memory directly. On Windows the process is paused only while a PSS snapshot is taken; on Linux it stays paused during the walk, which takes minutes on heaps of tens of GB. For a long-running node, take one `write_dump` and analyze the file.
- `heap_retention` keeps a visited-object map for its root search; `maxVisited` bounds its memory on very large heaps.
- Sizes from `heap_stats` are shallow (an object's own size, excluding what it references).
- dotTrace Sampling times are wall-clock milliseconds summed over all threads, so idle threads dominate a mostly idle process. `trace_hotspots` hides known blocking waits by default (`hideWaits`), but total times of callers still include them. Bracketed rows such as `[Native or optimized code]` are profiler pseudo-frames, and on Linux they also absorb time native threads spend blocked.
- `heap_stats` counts every object on the heap, including unreachable ones the GC has not collected yet. Use `heap_retention` to check whether instances are actually retained.
