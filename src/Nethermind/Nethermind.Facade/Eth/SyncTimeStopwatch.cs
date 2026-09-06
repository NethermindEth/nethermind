// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;

namespace Nethermind.Facade.Eth;

/// <summary>
/// Wall-clock stopwatch that runs only while the node is syncing and retains the accumulated total
/// after syncing stops, resuming (not restarting) if syncing begins again.
/// </summary>
public sealed class SyncTimeStopwatch
{
    private readonly Stopwatch _stopwatch = new();

    /// <summary>
    /// Starts or stops the underlying stopwatch to match <paramref name="isSyncing"/> and returns the
    /// total time spent syncing so far. The total is never reset, so it stays observable after sync completes.
    /// </summary>
    public TimeSpan UpdateAndGet(bool isSyncing)
    {
        if (!_stopwatch.IsRunning)
        {
            if (isSyncing) _stopwatch.Start();
        }
        else if (!isSyncing)
        {
            _stopwatch.Stop();
        }

        return _stopwatch.Elapsed;
    }
}
