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
        [ConfigItem(Description = "Enables in-memory pruning. Obsolete, use Mode instead.", DefaultValue = "false")]
        [Obsolete]
        public bool Enabled { get; set; }
        
        [ConfigItem(Description = "Sets pruning mode.", DefaultValue = "None")]
        PruningMode Mode { get; set; }
        
        [ConfigItem(Description = "'Memory' pruning: Pruning cache size in MB (amount if historical nodes data to store in cache - the bigger the cache the bigger the disk space savings).", DefaultValue = "512")]
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
            Description = "'Full' pruning: Defines how many parallel tasks and potentially used threads can be created by full pruning. 0 - no limit, 1 - full pruning will run on single thread, 16^N - First N levels of the state tree will be run in parallel.",
            DefaultValue = "16")]
        int FullPruningMaxDegreeOfParallelism { get; set; }
        
        
    }
}
