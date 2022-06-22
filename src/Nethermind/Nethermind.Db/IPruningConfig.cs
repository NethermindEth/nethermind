//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

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
            Description = "'Full' pruning: Defines how many parallel tasks and potentially used threads can be created by full pruning. 0 - number of logical processors, 1 - full pruning will run on single thread. " +
                          "Recommended value depends on the type of the node. If the node needs to be responsive (its RPC or Validator node) then recommended value is below the number of logical processors. " +
                          "If the node doesn't have much other responsibilities but needs to be reliably be able to follow the chain without any delays and produce live logs - the default value is recommended. " + 
                          "If the node doesn't have to be responsive, has very fast I/O (like NVME) and the shortest pruning time is to be achieved, this can be set to 2-3x of the number of logical processors.",
            DefaultValue = "0")]
        int FullPruningMaxDegreeOfParallelism { get; set; }

        [ConfigItem(Description = "In order to not exhaust disk writes, there is a minimum delay between allowed full pruning operations.", DefaultValue = "240")]
        int FullPruningMinimumDelayHours { get; set; }

        [ConfigItem(Description = "If enabled, Nethermind will shut down after a full prune completes. Useful for automated systems that change pruning modes from Full to Memory after a full prune.")]
        FullPruningCompletionBehavior ShutdownAfterFullPrune { get; set; }
    }
}
