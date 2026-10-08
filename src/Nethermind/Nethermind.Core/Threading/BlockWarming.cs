// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;

namespace Nethermind.Core.Threading;

/// <summary>
/// Whether a block's warm pass is running, so lower-priority work on the same block can leave the disk and the cores to
/// it until it ends.
/// </summary>
public static class BlockWarming
{
    private static readonly Lock StateLock = new();
    private static readonly ManualResetEventSlim Ended = new(initialState: true);
    private static int _active;

    public static bool IsActive => Volatile.Read(ref _active) > 0;

    public static void Begin()
    {
        using (StateLock.EnterScope())
        {
            if (_active++ == 0) Ended.Reset();
        }
    }

    public static void End()
    {
        using (StateLock.EnterScope())
        {
            if (--_active == 0) Ended.Set();
        }
    }

    /// <returns>Whether no warm pass was running by the time it returned.</returns>
    public static bool WaitForEnd(TimeSpan timeout) => Ended.Wait(timeout);
}
