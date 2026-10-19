// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Nethermind.Db.Lmdb;

/// <summary>
/// Process-wide page fault count, for correlating a slow LMDB operation against cold-page/disk-cache-miss cost
/// on the memory-mapped file - see PLAN-LMDB.md "Validation methodology", which called for exactly this
/// (Windows working-set/page-fault counters) from the start but never implemented it. "SSD activity near 0%"
/// during an earlier stall ruled out *sustained* disk throughput, but not a scattered burst of single-page
/// random reads, which can be real page-fault latency without showing up as sustained activity on a coarse
/// utilization graph.
/// </summary>
/// <remarks>
/// Windows-only (via <c>GetProcessMemoryInfo</c>/psapi.dll) - returns 0 on every other platform, including this
/// plugin's Linux CI/test environment, rather than throwing.
/// </remarks>
/// <remarks>
/// <see cref="CachedPageFaultCount"/> is sampled periodically by <see cref="LmdbDbFactoryDecorator"/>'s existing
/// GC-pause heartbeat thread (piggybacking on an already-running background tick rather than adding a new one)
/// and read here with a plain <see cref="Volatile.Read(ref long)"/> - deliberately not calling the real P/Invoke
/// on every single write's "before" baseline, since that cost (a real syscall) would itself skew the latency
/// this exists to measure. The live, uncached value is only read via <see cref="ReadNow"/> on the rare slow-event
/// path, where one extra syscall is immaterial. The up-to-~250ms staleness this trades away is acceptable for a
/// rough correlation signal, not a billing-grade counter - same tradeoff already accepted elsewhere in this
/// plugin's diagnostics.
/// </remarks>
internal static class WindowsProcessDiagnostics
{
    private static long _cachedPageFaultCount;

    internal static long CachedPageFaultCount => Volatile.Read(ref _cachedPageFaultCount);

    internal static void UpdateCachedPageFaultCount() => Volatile.Write(ref _cachedPageFaultCount, ReadNow());

    internal static long ReadNow()
    {
        if (!OperatingSystem.IsWindows()) return 0;

        PROCESS_MEMORY_COUNTERS counters = default;
        counters.cb = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS>();
        return GetProcessMemoryInfo(Process.GetCurrentProcess().Handle, out counters, counters.cb) ? counters.PageFaultCount : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_MEMORY_COUNTERS
    {
        public uint cb;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetProcessMemoryInfo(IntPtr hProcess, out PROCESS_MEMORY_COUNTERS counters, uint size);
}
