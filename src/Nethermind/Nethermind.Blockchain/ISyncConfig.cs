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

using Nethermind.Config;

namespace Nethermind.Blockchain
{
    public interface ISyncConfig : IConfig
    {
        [ConfigItem(Description = "Beam Sync - only for DEBUG / DEV - not working in prod yet.", DefaultValue = "false")]
        bool BeamSyncEnabled { get; set; }
        
        [ConfigItem(Description = "If set to 'true' then the Fast Sync (eth/63) synchronization algorithm will be used.", DefaultValue = "false")]
        bool FastSync { get; set; }
        
        [ConfigItem(Description = "If set to 'true' then in the Fast Sync mode blocks will be first downloaded from the provided PivotNumber downwards. This allows for parallelization of requests with many sync peers and with no need to worry about syncing a valid branch (syncing downwards to 0). You need to enter the pivot block number, hash and total difficulty from a trusted source (you can use etherscan and confirm with other sources if you wan to change it).", DefaultValue = "false")]
        bool FastBlocks { get; set; }
        
        [ConfigItem(Description = "If set to 'true' then in the Fast Blocks mode Nethermind generates smaller requests to avoid Geth from disconnecting. On the Geth heavy networks (mainnet) it is desired while on Parity or Nethermind heavy networks (Goerli, AuRa) it slows down the sync by a factor of ~4", DefaultValue = "true")]
        public bool UseGethLimitsInFastBlocks { get; set; }
        
        [ConfigItem(Description = "If set to 'true' then the block bodies will be downloaded in the Fast Sync mode.", DefaultValue = "true")]
        bool DownloadBodiesInFastSync { get; set; }
        
        [ConfigItem(Description = "If set to 'true' then the receipts will be downloaded in the Fast Sync mode. This will slow down the process by a few hours but will allow you to interact with dApps that execute extensive historical logs searches (like Maker CDPs).", DefaultValue = "true")]
        bool DownloadReceiptsInFastSync { get; set; }
        
        [ConfigItem(Description = "Total Difficulty of the pivot block for the Fast Blocks sync (not - this is total difficulty and not difficulty).", DefaultValue = "null")]
        string PivotTotalDifficulty { get; }
        
        [ConfigItem(Description = "Number of the pivot block for the Fast Blocks sync.", DefaultValue = "null")]
        string PivotNumber { get; }
        
        [ConfigItem(Description = "Hash of the pivot block for the Fast Blocks sync.", DefaultValue = "null")]
        string PivotHash { get; }
    }
}