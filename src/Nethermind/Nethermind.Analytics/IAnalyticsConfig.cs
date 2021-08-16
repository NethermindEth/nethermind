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

namespace Nethermind.Analytics
{
    [ConfigCategory(HiddenFromDocs = true)]
    public interface IAnalyticsConfig : IConfig
    {
        [ConfigItem(Description = "If 'false' then no analytics plugins will be loaded", DefaultValue = "false")]
        public bool PluginsEnabled { get; set; }
        
        [ConfigItem(Description = "If 'false' then transactions are not streamed by default to gRPC endpoints.", DefaultValue = "false")]
        public bool StreamTransactions { get; set; }

        [ConfigItem(Description = "If 'false' then blocks are not streamed by default to gRPC endpoints.", DefaultValue = "false")]
        public bool StreamBlocks { get; set; }
        
        [ConfigItem(Description = "If 'true' then all analytics will be also output to logger", DefaultValue = "false")]
        public bool LogPublishedData { get; set; }
    }
}
