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

using Nethermind.Config;

namespace Nethermind.Db.Blooms
{
    public interface IBloomConfig : IConfig
    {
        [ConfigItem(Description = "Defines whether the Bloom index is used. Bloom index speeds up rpc log searches.", DefaultValue = "true")]
        bool Index { get; set; }
        
        [ConfigItem(Description = "Defines multipliers for index levels. Can be tweaked per chain to boost performance.", DefaultValue = "[4, 8, 8]")]
        int[] IndexLevelBucketSizes { get; set; }
        
        [ConfigItem(Description = "Defines if migration statistics are to be calculated and output.", DefaultValue = "false")]
        bool MigrationStatistics { get; set; }
        
        [ConfigItem(Description = "Defines if migration of previously downloaded blocks to Bloom index will be done.", DefaultValue = "false")]
        bool Migration { get; set; }
    }
}
