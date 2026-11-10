// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Nethermind.BlockProfiler;

/// <summary>
/// User-space instructions and cycles retired by the calling thread, read from Linux hardware counters through
/// <c>perf_event_open</c>. Each processing scope opens and disposes its own counters.
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

    [ThreadStatic] private static Counters? t_counters;
    [ThreadStatic] private static int t_scopeDepth;
    private static volatile string? s_error;

    /// <summary>Why the counters are unavailable, or <c>null</c> while they work.</summary>
    public static string? Error => s_error;

    public readonly record struct Sample(ulong Instructions, ulong Cycles, bool Multiplexed)
    {
        public static Sample operator -(Sample end, Sample start) =>
            new(end.Instructions - start.Instructions, end.Cycles - start.Cycles, end.Multiplexed || start.Multiplexed);
    }

    internal static bool InScope => t_scopeDepth > 0;

    /// <summary>Begins a counter scope; the outermost scope owns the handles and nested scopes share them.</summary>
    internal static Scope BeginScope()
    {
        if (t_scopeDepth == 0 && t_counters is null && s_error is null)
        {
            t_counters = Open();
        }

        t_scopeDepth++;
        return new Scope();
    }

    /// <summary>Reads the calling thread's counters while a processing scope is active.</summary>
    public static bool TryRead(out Sample sample)
    {
        sample = default;
        Counters? counters = t_counters;
        if (t_scopeDepth == 0 || s_error is not null || counters is null) return false;

        if (!TryRead(counters.Instructions, out ulong instructions, out bool instructionsMultiplexed)
            || !TryRead(counters.Cycles, out ulong cycles, out bool cyclesMultiplexed))
        {
            s_error ??= $"read failed, errno {Marshal.GetLastPInvokeError()}";
            t_counters = null;
            counters.Dispose();
            return false;
        }

        sample = new Sample(instructions, cycles, instructionsMultiplexed || cyclesMultiplexed);
        return true;
    }

    private static void EndScope()
    {
        if (t_scopeDepth == 0) return;
        if (--t_scopeDepth != 0) return;

        t_counters?.Dispose();
        t_counters = null;
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
        if (instructions < 0) return Fail(Marshal.GetLastPInvokeError());
        PerfEventHandle instructionsHandle = new(instructions);

        int cycles = Open(syscallNumber, PerfCountHwCpuCycles);
        if (cycles < 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            instructionsHandle.Dispose();
            return Fail(errno);
        }

        return new Counters(instructionsHandle, new PerfEventHandle(cycles));

        static Counters? Fail(int errno)
        {
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

    private static bool TryRead(SafeHandle fd, out ulong value, out bool multiplexed)
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

    internal readonly struct Scope : IDisposable
    {
        public void Dispose() => EndScope();
    }

    private sealed class Counters(SafeHandle instructions, SafeHandle cycles) : IDisposable
    {
        public SafeHandle Instructions { get; } = instructions;
        public SafeHandle Cycles { get; } = cycles;

        public void Dispose()
        {
            Instructions.Dispose();
            Cycles.Dispose();
        }
    }

    private sealed class PerfEventHandle : SafeHandleMinusOneIsInvalid
    {
        public PerfEventHandle(int fd) : base(ownsHandle: true) => SetHandle((nint)fd);

        protected override bool ReleaseHandle() => close(handle.ToInt32()) == 0;
    }

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
    private static partial nint read(SafeHandle fd, void* buffer, nuint count);

    [LibraryImport("libc")]
    private static partial int close(int fd);
}
