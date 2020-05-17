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
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Serialization.Json;

namespace Nethermind.Blockchain.Synchronization
{
    public interface ISyncConfig : IConfig
    {
        [ConfigItem(Description = "If 'false' then the node does not download/process new blocks..", DefaultValue = "true")]
        bool SynchronizationEnabled { get; set; }
        
        [ConfigItem(Description = "Beam Sync - only for DEBUG / DEV - not working in prod yet.", DefaultValue = "false")]
        bool BeamSync { get; set; }

        [ConfigItem(Description = "If set to 'true' then the Fast Sync (eth/63) synchronization algorithm will be used.", DefaultValue = "false")]
        bool FastSync { get; set; }
        
        [ConfigItem(Description = "Relevant only if 'FastSync' is 'true'. If set to a value, then it will set a minimum height threshold limit up to which FullSync, if already on, will stay on when chain will be behind network. If this limit will be exceeded, it will switch back to FastSync. Please note that last 32 blocks will always be processed in FullSync, so setting it to less or equal to 32 will have no effect.", DefaultValue = "1024")]
        long? FastSyncCatchUpHeightDelta { get; set; }
        
        [ConfigItem(Description = "If set to 'true' then in the Fast Sync mode blocks will be first downloaded from the provided PivotNumber downwards. This allows for parallelization of requests with many sync peers and with no need to worry about syncing a valid branch (syncing downwards to 0). You need to enter the pivot block number, hash and total difficulty from a trusted source (you can use etherscan and confirm with other sources if you wan to change it).", DefaultValue = "false")]
        bool FastBlocks { get; set; }
        
        [ConfigItem(Description = "If set to 'true' then in the Fast Blocks mode Nethermind generates smaller requests to avoid Geth from disconnecting. On the Geth heavy networks (mainnet) it is desired while on Parity or Nethermind heavy networks (Goerli, AuRa) it slows down the sync by a factor of ~4", DefaultValue = "true")]
        public bool UseGethLimitsInFastBlocks { get; set; }
        
        [ConfigItem(Description = "If set to 'false' then beam sync will only download recent blocks.", DefaultValue = "true")]
        bool DownloadHeadersInFastSync { get; set; }
        
        [ConfigItem(Description = "If set to 'true' then the block bodies will be downloaded in the Fast Sync mode.", DefaultValue = "true")]
        bool DownloadBodiesInFastSync { get; set; }
        
        [ConfigItem(Description = "If set to 'true' then the receipts will be downloaded in the Fast Sync mode. This will slow down the process by a few hours but will allow you to interact with dApps that execute extensive historical logs searches (like Maker CDPs).", DefaultValue = "true")]
        bool DownloadReceiptsInFastSync { get; set; }
        
        [ConfigItem(Description = "Total Difficulty of the pivot block for the Fast Blocks sync (not - this is total difficulty and not difficulty).", DefaultValue = "null")]
        string PivotTotalDifficulty { get; }
        
        [ConfigItem(Description = "Number of the pivot block for the Fast Blocks sync.", DefaultValue = "null")]
        string PivotNumber { get; set; }
        
        [ConfigItem(Description = "Hash of the pivot block for the Fast Blocks sync.", DefaultValue = "null")]
        string PivotHash { get; set; }

        long PivotNumberParsed => LongConverter.FromString(PivotNumber ?? "0");

        UInt256 PivotTotalDifficultyParsed => UInt256.Parse(PivotTotalDifficulty ?? "0x0");

        Keccak PivotHashParsed => PivotHash == null ? null : new Keccak(Bytes.FromHexString(PivotHash));
    }
}
