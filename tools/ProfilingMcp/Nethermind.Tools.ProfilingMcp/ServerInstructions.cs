// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.ProfilingMcp;

internal static class ServerInstructions
{
    public const string Text = """
        Profiles .NET processes over EventPipe by default: CPU samples, GC and exception events, and heap snapshots. JetBrains dotTrace and ClrMD dump analysis are there for what EventPipe cannot do.

        Triage: list_dotnet_processes, then runtime_counters to see whether the problem is CPU, GC/allocations, or contention.

        Containers: when the request names a container, a node or a remote host ("my local hoodi node", "the node on node1"), find it with list_containers (filter by a word from the request; pass dockerHost for another engine) and pass container, plus dockerHost when not local, to every tool for that target, including the analysis of its traces and snapshots.
        PIDs and paths are then the container's; the first call for a container can take about 30 s. dotTrace is not available in containers; use cpu_sample.

        CPU / slowness:
        1. Running process: cpu_sample while the slow workload runs. It returns hotspots, GC pauses and thrown exceptions, and saves a trace.
           Launching an app, or needing exact call counts (Tracing): trace_start under dotTrace, drive the workload, then trace_snapshot.
           Use trace_attach only when dotTrace is specifically needed for a running process.
        2. The trace or snapshot path works in every step below.
        3. trace_hotspots sorted by own time finds where time is spent; sorted by total time finds expensive subtrees.
           Use include/exclude regexes to focus on application code.
        4. trace_callstacks on a hotspot shows which callers make it expensive.
        5. After a fix, profile the same workload again and run trace_compare.
        When a person wants to see the results, trace_report writes an HTML page with a flame graph and hotspot table; give them its path.
        EventPipe traces (*.nettrace) are analyzed on any OS. dotTrace snapshot analysis needs Windows; snapshots captured on Linux can be copied there (all *.dtp* files).
        Timeline snapshots cannot be analyzed; use Sampling or Tracing.
        Both measure wall time, so trace_hotspots hides blocking waits by default; look at own time of application methods.
        EventPipe sees managed threads only; native code shows as own time of the managed method that called it.
        To see inside native code (RocksDB, the runtime and GC, the kernel) or native threads, or to profile a process that is not .NET, use native_sample (Linux or a container). It measures CPU time, not waits, and its *.folded stacks work in the steps above.
        On Linux/macOS dotTrace can attach to a process once per run: the profiler is never detached because detaching crashes it.

        Memory growth / leaks:
        1. heap_stats on the PID (baseline), exercise the workload, heap_stats again.
        2. heap_diff the two ids; types that keep growing are leak candidates.
        3. heap_retention on a growing type shows what references it and the reference chains from GC roots, largest first.
        Process memory (RSS) far above the GC heap: native_memory (Linux or a container) splits it into GC heap, runtime, thread stacks, malloc and other native mmaps.
        GC pressure: alloc_sample shows which types are allocated most, by which methods and call stacks; include narrows it to one component.

        heap_stats and heap_retention read a PID over EventPipe: the runtime runs one full blocking GC (about 0.5 s for an 800 MB heap) and reports the objects that survive it.
        This works in containers and can be repeated. Nested types are named without their declaring type (Bucket[], Entry<...>); heap_retention shows who holds them.
        For field and static field names in reference chains, write_dump once and pass the dump path instead; dumps are as large as the process and pause it while written.
        Measurements are only meaningful for Release builds under a representative workload.
        """;
}
