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
using Nethermind.Int256;

namespace Nethermind.Consensus
{
    public interface IMiningConfig : IConfig
    {
        [ConfigItem(
            Description = "Defines whether the blocks should be produced.",
            DefaultValue = "false")]
        bool Enabled { get; set; }
        
        [ConfigItem(
            Description = "Block gas limit that the block producer should try to reach in the fastest possible way based on protocol rules." +
                          " NULL value means that the miner should follow other miners.",
            DefaultValue = "null")]
        long? TargetBlockGasLimit { get; set; }
        
        [ConfigItem(
            Description = "Minimum gas price for transactions accepted by the block producer.",
            DefaultValue = "1000000000")]
        UInt256 MinGasPrice { get; set; }
        
        [ConfigItem(
            Description = "Only used in NethDev. Setting this to true will change the difficulty of the block randomly within the constraints.",
            DefaultValue = "false")]
        bool RandomizedBlocks { get; set; }
    }
}
