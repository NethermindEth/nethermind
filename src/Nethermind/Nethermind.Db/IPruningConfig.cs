// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Config;

namespace Nethermind.Db
{
    [ConfigCategory(Description = "Configuration of the pruning parameters (pruning is the process of removing some of the intermediary state nodes - it saves some disk space but makes most of the historical state queries fail).")]
    public interface IPruningConfig : IConfig
    {
        [ConfigItem(Description = "Enables in-memory pruning. Obsolete, use Mode instead.", DefaultValue = "true", HiddenFromDocs = true)]
        [Obsolete]
        public bool Enabled { get; set; }

        [ConfigItem(Description = "Sets pruning mode. Possible values: 'None', 'Memory', 'Full', 'Hybrid'.", DefaultValue = "Hybrid")]
        PruningMode Mode { get; set; }

        [ConfigItem(Description = "'Memory' pruning: Pruning cache size in MB (amount if historical nodes data to store in cache - the bigger the cache the bigger the disk space savings).", DefaultValue = "1024")]
        long CacheMb { get; set; }

        [ConfigItem(
            Description = "'Memory' pruning: Defines how often blocks will be persisted even if not required by cache memory usage (the bigger the value the bigger the disk space savings)",
            DefaultValue = "8192")]
        long PersistenceInterval { get; set; }

        [ConfigItem(
            Description = "'Full' pruning: Defines threshold in MB to trigger full pruning, depends on 'Mode' and 'FullPruningTrigger'.",
            DefaultValue = "256000")]
        long FullPruningThresholdMb { get; set; }

        [ConfigItem(
            Description = "'Full' pruning: Defines trigger for full pruning, manuel trigger is always supported via admin_prune RPC call. " +
                          "Either size of StateDB or free space left on Volume where StateDB is located can be configured as auto triggers. " +
                          "Possible values: 'Manual', 'StateDbSize', 'VolumeFreeSpace'.",
            DefaultValue = "Manual")]
        FullPruningTrigger FullPruningTrigger { get; set; }

        [ConfigItem(
            Description = "'Full' pruning: Defines how many parallel tasks and potentially used threads can be created by full pruning. -1 - number of logical processors, 0 - 25% of logical processors, 1 - full pruning will run on single thread. " +
                          "Recommended value depends on the type of the node. If the node needs to be responsive (its RPC or Validator node) then recommended value is the default value or below is recommended. " +
                          "If the node doesn't have much other responsibilities but needs to be reliably be able to follow the chain without any delays and produce live logs - the default value or above is recommended. " +
                          "If the node doesn't have to be responsive, has very fast I/O (like NVME) and the shortest pruning time is to be achieved, this can be set to the number of logical processors (-1).",
            DefaultValue = "0")]
        int FullPruningMaxDegreeOfParallelism { get; set; }

        [ConfigItem(
            Description = "Set the memory budget used for the trie visit. Increasing this significantly reduces read iops requirement at expense of RAM. Default depend on network. Set to 0 to disable.",
            DefaultValue = "4000")]
        int FullPruningMemoryBudgetMb { get; set; }

        [ConfigItem(
            Description = "Full pruning uses low priority writes to prevent blocking block processing. If not needed, set this to true for faster full pruning.",
            DefaultValue = "false")]
        bool FullPruningDisableLowPriorityWrites { get; set; }

        [ConfigItem(Description = "In order to not exhaust disk writes, there is a minimum delay between allowed full pruning operations.", DefaultValue = "240")]
        int FullPruningMinimumDelayHours { get; set; }

        [ConfigItem(Description = "Determines what to do after Nethermind completes a full prune. " +
                                  "'None': does not take any special action. " +
                                  "'ShutdownOnSuccess': shuts Nethermind down if the full prune succeeded. " +
                                  "'AlwaysShutdown': shuts Nethermind down once the prune completes, whether it succeeded or failed.",
            DefaultValue = "None")]
        FullPruningCompletionBehavior FullPruningCompletionBehavior { get; set; }

        [ConfigItem(Description = "Enables available disk space check.", DefaultValue = "true")]
        bool AvailableSpaceCheckEnabled { get; set; }
    }
}
