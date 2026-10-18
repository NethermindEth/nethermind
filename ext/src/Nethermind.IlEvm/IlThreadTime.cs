// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;

namespace Nethermind.IlEvm;

/// <summary>
/// The CPU time the calling thread has run: what a compile costs. On a busy machine a compile thread waits for a core,
/// and that wait adds to its wall-clock time, not to this; a budget kept in wall-clock time would decline contracts, or
/// hold their compiles back, exactly when the machine is busiest.
/// </summary>
internal static class IlThreadTime
{
    /// <summary>The calling thread's CPU time in milliseconds, or NaN where the platform tells none; only differences taken on one thread mean anything.</summary>
    /// <remarks>Windows counts it per clock tick (15.6 ms, or the finest timer resolution any process asked for), Linux to the nanosecond.</remarks>
    public static double Ms()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return GetThreadTimes(GetCurrentThread(), out _, out _, out long kernel, out long user) ? (kernel + user) / 10_000.0 : double.NaN;
            if (OperatingSystem.IsLinux())
                return ClockGetTime(ThreadCpuTimeClock, out TimeSpec time) == 0 ? time.Seconds * 1_000.0 + time.Nanoseconds / 1_000_000.0 : double.NaN;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            IlEvmSettings.RecordCaught("thread time", exception);
        }

        return double.NaN;
    }

    /// <summary>CLOCK_THREAD_CPUTIME_ID.</summary>
    private const int ThreadCpuTimeClock = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct TimeSpec
    {
        public long Seconds;
        public long Nanoseconds;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);

    [DllImport("libc", EntryPoint = "clock_gettime")]
    private static extern int ClockGetTime(int clock, out TimeSpec time);
}
