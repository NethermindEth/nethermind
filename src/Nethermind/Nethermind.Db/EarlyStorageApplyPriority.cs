// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Db;

/// <summary>Scheduling priority of the thread that applies committed storage writes to the storage tries during execution.</summary>
public enum EarlyStorageApplyPriority
{
    /// <summary>SCHED_IDLE on Linux, so the thread runs only on cores nothing else wants; the lowest thread priority elsewhere.</summary>
    Idle,

    /// <summary>SCHED_OTHER with nice 10 on Linux; a below-normal thread priority elsewhere.</summary>
    Low,

    /// <summary>SCHED_OTHER with nice 0 on Linux; the normal thread priority elsewhere.</summary>
    Normal,
}
