// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core2.Api
{
    public class Syncing
    {
        public Syncing(bool isSyncing, SyncingStatus? syncStatus)
        {
            IsSyncing = isSyncing;
            SyncStatus = syncStatus;
        }

        public bool IsSyncing { get; }
        public SyncingStatus? SyncStatus { get; }
    }
}
