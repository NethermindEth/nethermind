/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using Nethermind.Config;

namespace Nethermind.Blockchain
{
    public interface ISyncConfig : IConfig
    {
        [ConfigItem(Description = "If set to 'true' then the Fast Sync (eth/63) synchronization algorithm will be used.", DefaultValue = "false")]
        bool FastSync { get; set; }
        
        [ConfigItem(Description = "If set to 'true' then in the Fast Sync mode blocks will be first downloaded from the provided PivotNumber downwards.", DefaultValue = "false")]
        bool FastBlocks { get; set; }
        
        [ConfigItem(Description = "If set to 'true' then the block bodies will be downloaded in the Fast Sync mode.", DefaultValue = "true")]
        bool DownloadBodiesInFastSync { get; set; }
        
        [ConfigItem(Description = "If set to 'true' then the receipts will be downloaded in the Fast Sync mode.", DefaultValue = "true")]
        bool DownloadReceiptsInFastSync { get; set; }
        
        [ConfigItem(Description = "Total Difficulty of the pivot block for the Fast Blocks sync.", DefaultValue = "null")]
        string PivotTotalDifficulty { get; }
        
        [ConfigItem(Description = "Number of the pivot block for the Fast Blocks sync.", DefaultValue = "null")]
        string PivotNumber { get; }
        
        [ConfigItem(Description = "Hash of the pivot block for the Fast Blocks sync.", DefaultValue = "null")]
        string PivotHash { get; }
    }
}