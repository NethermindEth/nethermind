// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        public long PersistenceInterval { get; set; } = Reorganization.PersistenceInterval;
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
        public int DirtyNodeShardBit { get; set; } = 8;
        public double PrunePersistedNodePortion { get; set; } = 0.05;
        public long PrunePersistedNodeMinimumTarget { get; set; } = 50.MiB();
    }
}
