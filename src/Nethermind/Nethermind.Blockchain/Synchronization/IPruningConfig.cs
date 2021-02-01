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

using Nethermind.Config;

namespace Nethermind.Blockchain.Synchronization
{
    [ConfigCategory(Description = "Configuration of the pruning parameters (pruning is the process of removing some of the intermediary state nodes - it saves some disk space but makes most of the historical state queries fail).")]
    public interface IPruningConfig : IConfig
    {
        [ConfigItem(Description = "Enables pruning.", DefaultValue = "false")]
        bool Enabled { get; set; }
        
        [ConfigItem(Description = "Pruning cache size in MB (amount if historical nodes data to store in cache - the bigger the cache the bigger the disk space savings).", DefaultValue = "512")]
        long CacheMb { get; set; }
        
        [ConfigItem(
            Description = "Defines how often blocks will be persisted even if not required by cache memory usage (the bigger the value the bigger the disk space savings)",
            DefaultValue = "8192")]
        long PersistenceInterval { get; set; }
    }
}
