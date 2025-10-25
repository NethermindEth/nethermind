// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;

namespace Nethermind.Db
{
    public class PruningConfig : IPruningConfig
    {
        public bool Enabled
        {
            get => Mode.IsMemory();
            set
            {
                if (value)
                {
                    Mode |= PruningMode.Memory;
                }
                else
                {
                    Mode &= ~PruningMode.Memory;
                }
            }
        }

        public PruningMode Mode { get; set; } = PruningMode.Hybrid;
        public long CacheMb { get; set; } = 1280;
        public long DirtyCacheMb { get; set; } = 1024;
        public long PersistenceInterval { get; set; } = 1;
        public long FullPruningThresholdMb { get; set; } = 256000;
        public FullPruningTrigger FullPruningTrigger { get; set; } = FullPruningTrigger.Manual;
        public int FullPruningMaxDegreeOfParallelism { get; set; }
        public int FullPruningMemoryBudgetMb { get; set; } = 4000;
        public bool FullPruningDisableLowPriorityWrites { get; set; } = false;
        public int FullPruningMinimumDelayHours { get; set; } = 240;
        public FullPruningCompletionBehavior FullPruningCompletionBehavior { get; set; } = FullPruningCompletionBehavior.None;
        public bool AvailableSpaceCheckEnabled { get; set; } = true;
        public double TrackedPastKeyCountMemoryRatio { get; set; } = 0.1;
        public bool TrackPastKeys { get; set; } = true;
        public int PruningBoundary { get; set; } = (int)Reorganization.MaxDepth;

        private int _dirtyNodeShardBit = 8;

        public int DirtyNodeShardBit
        {
            get => _dirtyNodeShardBit;
            set
            {
                // 30 because of the 1 << 31 become negative
                if (value is < 0 or > 30)
                {
                    throw new InvalidOperationException($"Shard bit count must be between 0 and 30.");
                }

                _dirtyNodeShardBit = value;
            }
        }

        public double PrunePersistedNodePortion { get; set; } = 0.05;
        public long PrunePersistedNodeMinimumTarget { get; set; } = 50.MiB();
        public long MaxUnpersistedBlockCount { get; set; } = 300; // About 1 hour on mainnet
        public long MinUnpersistedBlockCount { get; set; } = 8; // About slightly more than 1 minute
        public int MaxBufferedCommitCount { get; set; } = 128;
    }
}
