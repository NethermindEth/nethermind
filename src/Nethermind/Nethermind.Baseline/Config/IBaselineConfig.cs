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

namespace Nethermind.Baseline.Config
{
    [ConfigCategory(Description = "Configuration of the Baseline Protocol integration with Nethermind")]
    public interface IBaselineConfig : IConfig
    {
        [ConfigItem(Description = "If 'true' then the Baseline Module is enabled via JSON RPC", DefaultValue = "false")]
        bool Enabled { get; }

        [ConfigItem(HiddenFromDocs = true, DefaultValue = "false")]
        bool BaselineTreeDbCacheIndexAndFilterBlocks { get; set; }
        
        [ConfigItem(HiddenFromDocs = true, DefaultValue = "1024")]
        ulong BaselineTreeDbBlockCacheSize { get; set; }
        
        [ConfigItem(HiddenFromDocs = true, DefaultValue = "1024")]
        ulong BaselineTreeDbWriteBufferSize { get; set; }
        
        [ConfigItem(HiddenFromDocs = true, DefaultValue = "4")]
        uint BaselineTreeDbWriteBufferNumber { get; set; }

        [ConfigItem(HiddenFromDocs = true, DefaultValue = "false")]
        bool BaselineTreeMetadataDbCacheIndexAndFilterBlocks { get; set; }
        
        [ConfigItem(HiddenFromDocs = true, DefaultValue = "1024")]
        ulong BaselineTreeMetadataDbBlockCacheSize { get; set; }
        
        [ConfigItem(HiddenFromDocs = true, DefaultValue = "1024")]
        ulong BaselineTreeMetadataDbWriteBufferSize { get; set; }
        
        [ConfigItem(HiddenFromDocs = true, DefaultValue = "4")]
        uint BaselineTreeMetadataDbWriteBufferNumber { get; set; }
    }
}
