// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.ProfilingMcp;

internal static class ServerInstructions
{
    public const string Text = """
        Profiles .NET processes: CPU with JetBrains dotTrace, memory with the .NET diagnostics libraries (ClrMD, EventPipe).

        Triage: list_dotnet_processes, then runtime_counters to see whether the problem is CPU, GC/allocations, or contention.

        CPU / slowness:
        1. trace_start (launch) or trace_attach (running PID) with Sampling.
        2. Drive the slow workload, then trace_snapshot.
        3. trace_hotspots sorted by own time finds where time is spent; sorted by total time finds expensive subtrees.
           Use include/exclude regexes to focus on application code.
        4. trace_callstacks on a hotspot shows which callers make it expensive.
        5. After a fix, profile the same workload again and run trace_compare.
        When a person wants to see the results, trace_report writes an HTML page with a flame graph and hotspot table; give them its path.
        Snapshot analysis needs Windows; snapshots captured on Linux can be copied there (all *.dtp* files). Timeline snapshots cannot be analyzed; use Sampling or Tracing.
        Sampling measures wall time, so trace_hotspots hides blocking waits by default; look at own time of application methods.
        On Linux/macOS an attached process can be profiled once per run: the profiler is never detached because detaching crashes it.

        In containers without CAP_SYS_PTRACE, heap_stats and heap_retention on a PID fail: use write_dump and analyze the dump file.

        Memory growth / leaks:
        1. heap_stats on the PID (baseline), exercise the workload, heap_stats again.
        2. heap_diff the two ids; types that keep growing are leak candidates.
        3. heap_retention on a growing type shows what references it and the reference chain from a GC root.
        GC pressure: alloc_sample shows which types are allocated most; find the allocating code with trace_callstacks.

        heap_stats and heap_retention pause a live process while they read its heap. On a production-like process use write_dump once and analyze the dump file instead.
        Measurements are only meaningful for Release builds under a representative workload.
        """;
}
