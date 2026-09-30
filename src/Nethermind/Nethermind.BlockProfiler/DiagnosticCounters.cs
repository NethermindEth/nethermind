// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Nethermind.BlockProfiler;

/// <summary>
/// <c>NETHERMIND_COUNT_DIAG=1</c>: per-thread events that say where a window's cycles went, next to the instruction and
/// cycle counters. The hardware ones share the core's remaining counters, so the kernel rotates them and each delta is
/// scaled to the time it was counting; the software ones (context switches, migrations, page faults) need no counter.
/// </summary>
/// <remarks>
/// The raw hardware events are AMD Zen 4 encodings and are opened only on that family: the top-down dispatch-slot
/// split (six slots a cycle: lost to the front end, lost to the back end, dispatched, retired), L2 demand misses,
/// demand fills from DRAM, branch mispredictions and L1 DTLB misses that also missed the L2 TLB. APERF/MPERF come from
/// the kernel's <c>msr</c> PMU when it exists: their ratio is the running frequency over the reference one.
/// </remarks>
internal static unsafe partial class DiagnosticCounters
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("NETHERMIND_COUNT_DIAG") == "1";

    private const uint PerfTypeHardware = 0;
    private const uint PerfTypeSoftware = 1;
    private const uint PerfTypeRaw = 4;
    private const ulong PerfFlagExcludeKernel = 1UL << 5;
    private const ulong PerfFlagExcludeHv = 1UL << 6;
    private const ulong PerfFormatTotalTimeEnabled = 1;
    private const ulong PerfFormatTotalTimeRunning = 2;
    private const ulong PerfFlagFdCloexec = 8;

    private readonly record struct EventSpec(string Name, uint Type, ulong Config, bool UserOnly, bool Scaled);

    private static readonly EventSpec[] s_events = BuildEvents();
    [ThreadStatic] private static int[]? t_fds;
    private static string? s_opened;

    /// <summary>The events in field order, and which of them could not be opened.</summary>
    public static string Describe() => s_opened ?? "not opened yet";

    public static int Count => s_events.Length;

    /// <summary>Raw value, time enabled and time running for every event, three slots each.</summary>
    public static bool TryRead(Span<ulong> buffer)
    {
        if (!Enabled || buffer.Length < 3 * s_events.Length) return false;
        int[] fds = t_fds ??= Open();
        for (int i = 0; i < fds.Length; i++)
        {
            Span<ulong> slot = buffer.Slice(3 * i, 3);
            if (fds[i] < 0 || !ReadOne(fds[i], slot)) slot.Clear();
        }
        return true;
    }

    /// <summary>Formats the deltas between two <see cref="TryRead"/> buffers, hardware events scaled to full time.</summary>
    public static void AppendDeltas(StringBuilder text, ReadOnlySpan<ulong> start, ReadOnlySpan<ulong> end)
    {
        text.Append(" pmu=");
        for (int i = 0; i < s_events.Length; i++)
        {
            if (i > 0) text.Append('/');
            ulong value = end[3 * i] - start[3 * i];
            ulong enabled = end[3 * i + 1] - start[3 * i + 1];
            ulong running = end[3 * i + 2] - start[3 * i + 2];
            if (enabled == 0) text.Append('-');
            else if (!s_events[i].Scaled || running == enabled) text.Append(value);
            else if (running == 0) text.Append('?');
            else text.Append((ulong)(value * ((double)enabled / running)));
        }
        // How much of the window the rotated hardware events were counting, in percent (lowest of them).
        double coverage = 1;
        for (int i = 0; i < s_events.Length; i++)
        {
            if (!s_events[i].Scaled) continue;
            ulong enabled = end[3 * i + 1] - start[3 * i + 1];
            if (enabled == 0) continue;
            coverage = Math.Min(coverage, (double)(end[3 * i + 2] - start[3 * i + 2]) / enabled);
        }
        text.Append(" pmucov=").Append((int)(coverage * 100));
    }

    private static EventSpec[] BuildEvents()
    {
        List<EventSpec> events = [];
        if (IsZen4())
        {
            // de_no_dispatch_per_slot.no_ops_from_frontend / .backend_stalls, de_src_op_disp.all, ex_ret_ops.
            events.Add(new("fe", PerfTypeRaw, 0x1_0000_01A0, true, true));
            events.Add(new("be", PerfTypeRaw, 0x1_0000_1EA0, true, true));
            events.Add(new("disp", PerfTypeRaw, 0x07AA, true, true));
            events.Add(new("ret", PerfTypeRaw, 0x00C1, true, true));
            // cache-misses maps to L2 demand misses (l2_cache_req_stat.ic_dc_miss_in_l2) on Zen.
            events.Add(new("l2m", PerfTypeHardware, 3, true, true));
            // ls_dmnd_fills_from_sys.dram_io_near | .dram_io_far.
            events.Add(new("dram", PerfTypeRaw, 0x4843, true, true));
            events.Add(new("brm", PerfTypeHardware, 5, true, true));
            // ls_l1_d_tlb_miss.all_l2_miss: page walks.
            events.Add(new("dtlb", PerfTypeRaw, 0xF045, true, true));
        }
        else
        {
            events.Add(new("l2m", PerfTypeHardware, 3, true, true));
            events.Add(new("brm", PerfTypeHardware, 5, true, true));
        }
        events.Add(new("cs", PerfTypeSoftware, 3, false, false));
        events.Add(new("mig", PerfTypeSoftware, 4, false, false));
        events.Add(new("minflt", PerfTypeSoftware, 5, false, false));
        events.Add(new("majflt", PerfTypeSoftware, 6, false, false));
        uint msrType = ReadPmuType("msr");
        if (msrType != uint.MaxValue)
        {
            // The msr PMU refuses mode filters; APERF and MPERF tick in user and kernel mode alike.
            events.Add(new("aperf", msrType, 1, false, false));
            events.Add(new("mperf", msrType, 2, false, false));
        }
        return [.. events];
    }

    private static int[] Open()
    {
        int[] fds = new int[s_events.Length];
        StringBuilder described = new();
        for (int i = 0; i < s_events.Length; i++)
        {
            EventSpec spec = s_events[i];
            PerfEventAttr attr = new()
            {
                Type = spec.Type,
                Size = (uint)sizeof(PerfEventAttr),
                Config = spec.Config,
                ReadFormat = PerfFormatTotalTimeEnabled | PerfFormatTotalTimeRunning,
                Flags = spec.UserOnly ? PerfFlagExcludeKernel | PerfFlagExcludeHv : 0,
            };
            fds[i] = (int)syscall(298, &attr, 0, -1, -1, PerfFlagFdCloexec);
            if (described.Length > 0) described.Append('/');
            described.Append(spec.Name);
            if (fds[i] < 0) described.Append("(errno ").Append(Marshal.GetLastPInvokeError()).Append(')');
        }
        s_opened ??= described.ToString();
        return fds;
    }

    private static bool ReadOne(int fd, Span<ulong> slot)
    {
        fixed (ulong* buffer = slot)
        {
            return read(fd, buffer, 3 * sizeof(ulong)) == 3 * sizeof(ulong);
        }
    }

    private static bool IsZen4()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64) return false;
        try
        {
            string vendor = string.Empty;
            int family = -1, model = -1;
            foreach (string line in File.ReadLines("/proc/cpuinfo"))
            {
                if (line.Length == 0) break;
                int colon = line.IndexOf(':');
                if (colon < 0) continue;
                string key = line[..colon].Trim();
                string value = line[(colon + 1)..].Trim();
                if (key == "vendor_id") vendor = value;
                else if (key == "cpu family") family = int.Parse(value, CultureInfo.InvariantCulture);
                else if (key == "model") model = int.Parse(value, CultureInfo.InvariantCulture);
            }
            // Family 19h: Genoa 10h-1Fh, Raphael and Phoenix 60h-7Fh, Bergamo and Siena A0h-AFh.
            return vendor == "AuthenticAMD" && family == 0x19 && model is (>= 0x10 and <= 0x1F) or (>= 0x60 and <= 0xAF);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
            return false;
        }
    }

    private static uint ReadPmuType(string pmu)
    {
        try
        {
            return uint.Parse(File.ReadAllText($"/sys/bus/event_source/devices/{pmu}/type").Trim(), CultureInfo.InvariantCulture);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
            return uint.MaxValue;
        }
    }

    /// <summary>The leading <c>PERF_ATTR_SIZE_VER0</c> bytes of <c>struct perf_event_attr</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PerfEventAttr
    {
        public uint Type;
        public uint Size;
        public ulong Config;
        public ulong SamplePeriod;
        public ulong SampleType;
        public ulong ReadFormat;
        public ulong Flags;
        public uint WakeupEvents;
        public uint BpType;
        public ulong Config1;
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial long syscall(long number, PerfEventAttr* attr, int pid, int cpu, int groupFd, ulong flags);

    [LibraryImport("libc", SetLastError = true)]
    private static partial nint read(int fd, void* buffer, nuint count);
}
