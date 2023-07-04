// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        public long CacheMb { get; set; } = 1024;
        public long PersistenceInterval { get; set; } = 8192;
        public long FullPruningThresholdMb { get; set; } = 256000;
        public FullPruningTrigger FullPruningTrigger { get; set; } = FullPruningTrigger.Manual;
        public int FullPruningMaxDegreeOfParallelism { get; set; }
        public int FullPruningMemoryBudgetMb { get; set; } = 4000;
        public bool FullPruningDisableLowPriorityWrites { get; set; } = false;
        public int FullPruningMinimumDelayHours { get; set; } = 240;
        public FullPruningCompletionBehavior FullPruningCompletionBehavior { get; set; } = FullPruningCompletionBehavior.None;
        public bool AvailableSpaceCheckEnabled { get; set; } = true;
    }
}
