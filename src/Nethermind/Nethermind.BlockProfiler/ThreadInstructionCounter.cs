// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Nethermind.BlockProfiler;

/// <summary>
/// User-space instructions and cycles retired by the calling thread, read from Linux hardware counters through
/// <c>perf_event_open</c>. Each thread opens its own counters on first use and keeps them for its lifetime.
/// </summary>
/// <remarks>
/// Kernel time is excluded on purpose: page faults, reads and futex waits depend on the page cache and on
/// scheduling, while the user-space instruction count of the same work on the same data repeats run to run.
/// </remarks>
internal static unsafe partial class ThreadInstructionCounter
{
    private const uint PerfTypeHardware = 0;
    private const ulong PerfCountHwCpuCycles = 0;
    private const ulong PerfCountHwInstructions = 1;
    private const ulong PerfFlagExcludeKernel = 1UL << 5;
    private const ulong PerfFlagExcludeHv = 1UL << 6;
    private const ulong PerfFormatTotalTimeEnabled = 1;
    private const ulong PerfFormatTotalTimeRunning = 2;
    private const ulong PerfFlagFdCloexec = 8;

    // Opened on a thread's first read and kept for its life, which suits the dedicated processing thread of the
    // deterministic mode; without it, each retired pool thread that counted leaves its two descriptors open.
    [ThreadStatic] private static Counters? t_counters;
    private static volatile string? s_error;

    /// <summary>Why the counters are unavailable, or <c>null</c> while they work.</summary>
    public static string? Error => s_error;

    public readonly record struct Sample(ulong Instructions, ulong Cycles, bool Multiplexed)
    {
        public static Sample operator -(Sample end, Sample start) =>
            new(end.Instructions - start.Instructions, end.Cycles - start.Cycles, end.Multiplexed || start.Multiplexed);
    }

    /// <summary>Reads the calling thread's counters, opening them on the thread's first call.</summary>
    public static bool TryRead(out Sample sample)
    {
        sample = default;
        if (s_error is not null) return false;

        Counters? counters = t_counters ??= Open();
        if (counters is null) return false;

        if (!TryRead(counters.Instructions, out ulong instructions, out bool instructionsMultiplexed)
            || !TryRead(counters.Cycles, out ulong cycles, out bool cyclesMultiplexed))
        {
            s_error ??= $"read failed, errno {Marshal.GetLastPInvokeError()}";
            return false;
        }

        sample = new Sample(instructions, cycles, instructionsMultiplexed || cyclesMultiplexed);
        return true;
    }

    private static Counters? Open()
    {
        if (!OperatingSystem.IsLinux())
        {
            s_error ??= "perf_event_open needs Linux";
            return null;
        }

        long syscallNumber = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => 298,
            Architecture.Arm64 or Architecture.RiscV64 => 241,
            _ => -1
        };
        if (syscallNumber < 0)
        {
            s_error ??= $"no perf_event_open syscall number for {RuntimeInformation.ProcessArchitecture}";
            return null;
        }

        int instructions = Open(syscallNumber, PerfCountHwInstructions);
        if (instructions < 0) return Fail(-1);
        int cycles = Open(syscallNumber, PerfCountHwCpuCycles);
        if (cycles < 0) return Fail(instructions);
        return new Counters(instructions, cycles);

        static Counters? Fail(int openedFd)
        {
            int errno = Marshal.GetLastPInvokeError();
            if (openedFd >= 0) close(openedFd);
            s_error ??= $"perf_event_open failed, errno {errno}, perf_event_paranoid {ReadParanoid()}";
            return null;
        }
    }

    private static int Open(long syscallNumber, ulong config)
    {
        PerfEventAttr attr = new()
        {
            Type = PerfTypeHardware,
            Size = (uint)sizeof(PerfEventAttr),
            Config = config,
            ReadFormat = PerfFormatTotalTimeEnabled | PerfFormatTotalTimeRunning,
            Flags = PerfFlagExcludeKernel | PerfFlagExcludeHv,
        };
        // pid 0 and cpu -1: the calling thread, on whichever CPU it runs.
        return (int)syscall(syscallNumber, &attr, 0, -1, -1, PerfFlagFdCloexec);
    }

    private static bool TryRead(int fd, out ulong value, out bool multiplexed)
    {
        // value, time enabled, time running: the two times differ once the kernel multiplexed the counter.
        ulong* buffer = stackalloc ulong[3];
        if (read(fd, buffer, 3 * sizeof(ulong)) != 3 * sizeof(ulong))
        {
            value = 0;
            multiplexed = false;
            return false;
        }

        value = buffer[0];
        multiplexed = buffer[1] != buffer[2];
        return true;
    }

    private static string ReadParanoid()
    {
        try
        {
            return File.ReadAllText("/proc/sys/kernel/perf_event_paranoid").Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "unreadable";
        }
    }

    private sealed record Counters(int Instructions, int Cycles);

    /// <summary>The leading <c>PERF_ATTR_SIZE_VER0</c> bytes of <c>struct perf_event_attr</c>, all this needs.</summary>
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

    [LibraryImport("libc")]
    private static partial int close(int fd);
}
