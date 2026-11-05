# ProfilingMcp

An [MCP](https://modelcontextprotocol.io) server that lets an AI agent profile .NET processes, locally or in Docker containers. By default it uses EventPipe, the runtime's own diagnostics channel, for CPU samples, GC and exception events, and heap snapshots: it needs no profiler or ptrace, works inside containers, and can be repeated on a live process. JetBrains dotTrace (launching apps, exact call counts) and ClrMD dump analysis (field names) cover what EventPipe cannot. Tool results are plain-text tables an agent can read directly.

New here? Start with [Getting started](#getting-started).

## Getting started

These steps take you from a fresh clone to an AI agent profiling a process. They take about five minutes, most of it the first build.

### 1. Install the prerequisites

| What | Why | Check |
| --- | --- | --- |
| [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) | Builds the server; its runtime runs it | `dotnet --version` prints `10.x` |
| Git | Gets the source | `git --version` |
| An MCP client: [Claude Code](https://docs.claude.com/en/docs/claude-code), Claude Desktop, VS Code, Cursor, or any client that starts stdio servers | Runs the agent that calls the tools | |
| Docker (optional) | Profiling processes in containers | `docker version` shows a server |

The machine running the agent needs no profiler. dotTrace's command-line tools download on first use (see [Requirements](#requirements)), and container profiling brings its own Linux tools in a sidecar image.

### 2. Get the source

```bash
git clone https://github.com/NethermindEth/nethermind.git
cd nethermind
```

The server needs only `tools/ProfilingMcp` and the repository's shared build files, so a shallow clone (`--depth 1`) is enough.

### 3. Build and install the server

Publish to a folder outside the repository. Choose a short path: the dotTrace tools fail on deeply nested Windows folders.

```bash
# Windows (PowerShell or Git Bash)
dotnet publish tools/ProfilingMcp/Nethermind.Tools.ProfilingMcp -c Release -o "$LOCALAPPDATA/ProfilingMcp/server"

# Linux and macOS
dotnet publish tools/ProfilingMcp/Nethermind.Tools.ProfilingMcp -c Release -o ~/.local/share/ProfilingMcp/server
```

The install folder now holds:

- `Nethermind.Tools.ProfilingMcp.exe` on Windows, or `Nethermind.Tools.ProfilingMcp` elsewhere: the server, which runs on the .NET 10 runtime installed with the SDK.
- `linux-x64/`: a self-contained Linux build of the same server, which container profiling runs in a sidecar.
- `sidecar.Dockerfile`: builds the sidecar image when no published one matches.

Agents start the server from this folder and keep it running, so on Windows a running session locks its files. To update later, publish to a new folder or close every session first; see [Updating](#updating).

### 4. Register the server with your agent

The server speaks MCP over stdio: the client starts the executable and talks to it over standard input and output. It takes no arguments.

**Claude Code** (the terminal, desktop app and IDE extensions share one configuration). Register it for your user, so every project and folder can use it:

```bash
# Windows
claude mcp add -s user profiling -- "$LOCALAPPDATA/ProfilingMcp/server/Nethermind.Tools.ProfilingMcp.exe"

# Linux and macOS
claude mcp add -s user profiling -- ~/.local/share/ProfilingMcp/server/Nethermind.Tools.ProfilingMcp
```

Use `-s project` instead to share the registration through the repository's `.mcp.json`. To pass [settings](#settings), add `-e NAME=value` before `--`.

**Claude Desktop.** Open Settings › Developer › Edit Config, which opens `claude_desktop_config.json` (in `%APPDATA%\Claude` on Windows, `~/Library/Application Support/Claude` on macOS), and add:

```json
{
  "mcpServers": {
    "profiling": {
      "command": "C:\\Users\\<you>\\AppData\\Local\\ProfilingMcp\\server\\Nethermind.Tools.ProfilingMcp.exe"
    }
  }
}
```

Restart Claude Desktop afterwards.

**VS Code** (GitHub Copilot agent mode). Add the server to `.vscode/mcp.json` in a workspace, or run "MCP: Open User Configuration" to make it available in every workspace:

```json
{
  "servers": {
    "profiling": {
      "type": "stdio",
      "command": "/home/<you>/.local/share/ProfilingMcp/server/Nethermind.Tools.ProfilingMcp"
    }
  }
}
```

**Cursor** and other clients that use the `mcpServers` format take the same entry as Claude Desktop; for Cursor it goes in `~/.cursor/mcp.json`. Settings go in an `env` object next to `command`.

Always give the absolute path to the executable: clients do not expand `~` or environment variables in a JSON configuration.

### 5. Check that it is connected

Clients connect to MCP servers when a session starts, so start a new session after registering.

- **Claude Code:** `claude mcp list` should show `profiling: <path> - ✔ Connected`. Inside a session, `/mcp` lists the server and its tools.
- **Other clients:** the server should appear in the client's MCP or tools list, with tools such as `cpu_sample` and `heap_stats`.

Then ask the agent something that needs a tool:

> List the .NET processes running on this machine.

The agent calls `list_dotnet_processes` and shows PIDs, names and working sets. If your client asks you to approve every tool call, see [Approving tool calls](#approving-tool-calls).

### 6. Profile something

Ask in plain words: the server's built-in instructions tell the agent which tools to use and in what order. For example:

- *"My app with PID 1234 is slow. Find where it spends its CPU time while I run the load test."*
- *"The memory of the `nethermind-hoodi` container keeps growing. Is it managed or native, and what is holding it?"*
- *"Profile the node under `eth_getLogs` load for 60 s and give me a flame graph I can open."*
- *"Compare CPU before and after my change."* The agent runs `trace_compare` on two traces.

Results are text tables that the agent reads directly. To look yourself, ask for a report: `trace_report` (a CPU flame graph) and `native_memory` (a memory treemap) write self-contained HTML pages, and the agent gives you their paths. For a container the pages are inside the sidecar, so the agent also gives the `docker cp` command that copies them out.

To profile a container, name it; nothing is set up per container. The first call for a container prepares a sidecar for it, which can take a minute or two when it has to build the sidecar image (see [Profiling containers](#profiling-containers)); later calls start in seconds.

### Approving tool calls

By default Claude Code asks before each tool call. The tools observe and change nothing in the target's code or settings, but some affect it:

- heap reads pause it for one full GC;
- `write_dump` pauses it while the dump is written;
- `trace_start` launches a program.

To allow all of the server's tools, add `mcp__profiling` to `permissions.allow` in `~/.claude/settings.json`. To allow single tools, add entries such as `mcp__profiling__cpu_sample` instead.

### Settings

Settings are environment variables in the server's environment. Pass them with `-e` to `claude mcp add`, or in the client's `env` object. Normal use needs none of them.

| Variable | Default | Effect |
| --- | --- | --- |
| `PROFILING_MCP_HOME` | `%LOCALAPPDATA%\ProfilingMcp` on Windows, `~/.local/share/ProfilingMcp` elsewhere | Where traces, reports, heap statistics, dumps and the downloaded dotTrace tools go. Keep it short on Windows |
| `DOTTRACE_TOOLS_DIR` | Downloaded on first use | A folder with an existing `dottrace` or `dottrace.exe` to use instead |
| `PROFILING_MCP_SIDECAR_IMAGE` | Built locally or pulled | `local` to always build the sidecar image from this install, or the name of an image to use instead |

### Updating

1. Pull the source and publish to a **new** folder, such as `server-2`: on Windows, running sessions keep the current folder locked. Alternatively, close every agent session and publish over the old folder.
2. If the path changed, register the server again: run `claude mcp remove -s user profiling`, then repeat step 4.
3. Start a new session. The first container call after an update prepares a sidecar image for the new version. Old sidecars keep running idle until you remove them with `docker rm -f profmcp-<container>-<image id>`.

### Uninstalling

```bash
claude mcp remove -s user profiling
```

Then delete the install folder and the data folder (`PROFILING_MCP_HOME`). If you profiled containers, also remove:

- the sidecars: list them with `docker ps -a --filter name=profmcp-`, then `docker rm -f` each;
- their volumes, with `docker volume rm profmcp-home-<container>`;
- their images, with `docker image rm profiling-mcp:<hash>`.

### Troubleshooting

| Symptom | Cause and fix |
| --- | --- |
| The server shows as failed or not connected | Check that the registered path is absolute and ends in the executable. Run the executable by hand: it should log "Application started" and wait for input (Ctrl+C stops it). An error about a missing framework means the .NET 10 runtime is not installed |
| New tools or fixes do not show up | Clients connect at session start, so start a new session after publishing. Check that the registration points to the folder you published to |
| `dotnet publish` fails with "the file is locked by" | A running session uses that folder. Publish to a new folder, or close the sessions first |
| `list_dotnet_processes` misses a process | The process runs as another user (run the agent as that user, or as root or administrator), was started with `DOTNET_EnableDiagnostics=0`, or runs in a container, in which case pass `container` instead |
| A container call fails to start the sidecar | `docker version` must reach the engine from the agent's environment, and the engine must be `linux/amd64`. Building the sidecar image the first time needs network access |
| `native_sample` fails in a container | The engine's kernel is older than 5.8, so the sidecar cannot get `PERFMON`. `cpu_sample` still works |
| dotTrace exits with code -1 and no message | The install or data folder path is too long on Windows. Set a short `PROFILING_MCP_HOME` |
| dotTrace snapshots cannot be analyzed on Linux or macOS | JetBrains ships `Reporter.exe` for Windows only. Copy the `*.dtp*` files to Windows, or use `cpu_sample`, whose traces are analyzed on any OS |

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
| `native_memory` | Split a Linux process's resident memory into the GC heap by generation, the runtime's own heaps and JIT code, thread stacks, glibc malloc, other anonymous mmaps (such as RocksDB's jemalloc) and mapped files, and write an HTML page with a zoomable treemap down to single mappings, files and threads. Reads `/proc` and the runtime without pausing the process |
| `heap_retention` | Who references instances of a type, and the shortest chains from GC roots, largest retained bytes first. Chains from a dump also name fields and static fields |
| `alloc_sample` | Allocated bytes per type, allocating method and call stack over a window, from allocation tick events; `include` keeps the allocations with a matching frame |
| `write_dump` | A full-heap dump for offline `heap_stats`/`heap_retention` |

The server's instructions describe the intended workflows (CPU hotspots, leaks, GC pressure, native code and memory) to the agent.

## Requirements

- .NET 10 SDK.
- The dotTrace command-line tools. On first use the server downloads the free [`JetBrains.dotTrace.CommandLineTools`](https://www.nuget.org/packages?q=JetBrains.dotTrace.CommandLineTools) package for the current OS and architecture. Its licence needs no key or subscription and allows redistribution. To use an existing copy, set `DOTTRACE_TOOLS_DIR` to the folder containing `dottrace.exe` (Windows) or `dottrace` (Linux/macOS).
- Analyzing dotTrace snapshots (`trace_hotspots`, `trace_callstacks`, `trace_compare`, `trace_report`) needs Windows, because JetBrains ships `Reporter.exe` only there. On Linux and macOS the server can capture snapshots; copy the `*.dtp*` files to Windows to analyze them. EventPipe traces from `cpu_sample` are analyzed on any OS.
- Timeline profiling on Windows needs the JetBrains ETW Host Service, and its snapshots cannot be analyzed by Reporter. Prefer Sampling.
- `native_memory` needs Linux and ptrace access to the process, which the sidecar has.
- `native_sample` needs Linux and `perf`: the sidecar image includes it, and on a Linux host it comes from `linux-perf` or `linux-tools`. Sampling another process needs `CAP_PERFMON` or root, or a `kernel.perf_event_paranoid` of 1 or less.
- The memory tools work on Windows and Linux. Reading a process over EventPipe needs access to its diagnostics socket (the same user, or root), not ptrace.

Snapshots, heap statistics and dumps go to `%LOCALAPPDATA%\ProfilingMcp` (`~/.local/share/ProfilingMcp` on Linux), or `PROFILING_MCP_HOME` if set. Keep this path short on Windows: the dotTrace console tools fail silently with exit code -1 when started from deeply nested folders.

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
