// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.ScopeProvider;

/// <summary>The calls that set the scheduling of the calling thread.</summary>
internal interface IThreadSchedulingCalls
{
    bool IsLinux { get; }

    /// <summary>sched_setscheduler for the calling thread with static priority 0.</summary>
    bool TrySetSchedulerPolicy(int policy, out int errno);

    /// <summary>The kernel id of the calling thread from syscall gettid, or -1 where this process cannot make that call.</summary>
    int GetThreadId();

    /// <summary>setpriority(PRIO_PROCESS, tid, nice), which on Linux sets the nice value of that one thread.</summary>
    bool TrySetThreadNice(int tid, int nice, out int errno);

    void SetManagedPriority(ThreadPriority priority);
}
