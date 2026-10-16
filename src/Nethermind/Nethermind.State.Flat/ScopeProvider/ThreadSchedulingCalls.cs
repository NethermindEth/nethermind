// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;

namespace Nethermind.State.Flat.ScopeProvider;

/// <summary>The scheduling calls of the operating system: libc on Linux, <see cref="Thread.Priority"/> anywhere.</summary>
internal sealed class ThreadSchedulingCalls : IThreadSchedulingCalls
{
    public static readonly ThreadSchedulingCalls Instance = new();

    private const int PrioProcess = 0;
    private const long SysGettidX64 = 186;
    private const long SysGettidArm64 = 178;

    public bool IsLinux => OperatingSystem.IsLinux();

    public bool TrySetSchedulerPolicy(int policy, out int errno)
    {
        SchedParam param = default;
        // pid 0 is the calling thread.
        bool set = sched_setscheduler(0, policy, ref param) == 0;
        errno = set ? 0 : Marshal.GetLastPInvokeError();
        return set;
    }

    public int GetThreadId()
    {
        long number = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => SysGettidX64,
            Architecture.Arm64 => SysGettidArm64,
            _ => -1
        };
        return number < 0 ? -1 : (int)syscall(number);
    }

    public bool TrySetThreadNice(int tid, int nice, out int errno)
    {
        bool set = setpriority(PrioProcess, (uint)tid, nice) == 0;
        errno = set ? 0 : Marshal.GetLastPInvokeError();
        return set;
    }

    public void SetManagedPriority(ThreadPriority priority) => Thread.CurrentThread.Priority = priority;

    [StructLayout(LayoutKind.Sequential)]
    private struct SchedParam
    {
        public int SchedPriority;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int sched_setscheduler(int pid, int policy, ref SchedParam param);

    [DllImport("libc", SetLastError = true)]
    private static extern long syscall(long number);

    [DllImport("libc", SetLastError = true)]
    private static extern int setpriority(int which, uint who, int prio);
}
