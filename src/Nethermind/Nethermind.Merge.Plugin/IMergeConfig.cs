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
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Merge.Plugin
{
    public interface IMergeConfig : IConfig
    {
        [ConfigItem(
            Description = "Defines whether the Merge plugin is enabled bundles are allowed.",
            DefaultValue = "false")]
        bool Enabled { get; set; }
        
        [ConfigItem(Description = "Account to be used by the block author / coinbase.", DefaultValue = "")]
        public string BlockAuthorAccount { get; set; }
        
        [ConfigItem(Description = "Terminal total difficulty used for transition process.", DefaultValue = "null")]
        public string? TerminalTotalDifficulty { get; set; }
        
        [ConfigItem(DisabledForCli = true, HiddenFromDocs = true, DefaultValue = "0")]
        UInt256? TerminalTotalDifficultyParsed => string.IsNullOrWhiteSpace(TerminalTotalDifficulty) ? null : UInt256.Parse(TerminalTotalDifficulty);
        
        [ConfigItem(Description = "Terminal PoW block hash used for transition process.", DefaultValue = "0x0000000000000000000000000000000000000000000000000000000000000000")]
        public Keccak TerminalBlockHash { get; set; }
        
        [ConfigItem(Description = "Terminal PoW block number used for transition process.")]
        public long? TerminalBlockNumber { get; set; }
    }
}
