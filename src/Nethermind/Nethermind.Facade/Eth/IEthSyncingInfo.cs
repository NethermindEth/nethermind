// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.Facade.Eth
{
    public interface IEthSyncingInfo
    {
        SyncingResult GetFullInfo();

        bool IsSyncing();

        /// <summary>
        /// Advances the sync-duration stopwatch and returns the total wall-clock time spent syncing so far.
        /// </summary>
        /// <remarks>
        /// The value is retained after sync completes (it does not reset to zero), so callers can read the
        /// final sync duration. If the node later falls behind and re-syncs, the stopwatch resumes accumulating.
        /// </remarks>
        TimeSpan UpdateAndGetSyncTime();

        SyncMode SyncMode { get; }
    }
}
