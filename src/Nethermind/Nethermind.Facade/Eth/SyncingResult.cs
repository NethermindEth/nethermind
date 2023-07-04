// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.Facade.Eth
{
    public struct SyncingResult
    {
        public static SyncingResult NotSyncing = new();

        public bool IsSyncing { get; set; }
        public long StartingBlock { get; set; }
        public long CurrentBlock { get; set; }
        public long HighestBlock { get; set; }
        public SyncMode SyncMode { get; set; }

        public override string ToString()
        {
            return $"IsSyncing: {IsSyncing}, StartingBlock: {StartingBlock}, CurrentBlock {CurrentBlock}, HighestBlock {HighestBlock}";
        }
    }
}
