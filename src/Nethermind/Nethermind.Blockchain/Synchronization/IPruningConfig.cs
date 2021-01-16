//  Copyright (c) 2018 Demerzel Solutions Limited
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
    public interface IPruningConfig : IConfig
    {
        [ConfigItem(Description = "Enables pruning (beta).", DefaultValue = "false")]
        bool Enabled { get; set; }
        
        [ConfigItem(Description = "Pruning cache size in MB (beta).", DefaultValue = "512")]
        long PruningCacheMb { get; set; }
        
        [ConfigItem(
            Description = "Defines how often blocks will be persisted even if not required by cache memory usage (beta)",
            DefaultValue = "8192")]
        long PruningPersistenceInterval { get; set; }
    }
}