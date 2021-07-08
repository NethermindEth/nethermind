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

namespace Nethermind.MevSearcher
{
    public interface IMevSearcherConfig : IConfig
    {
        [ConfigItem(
            Description = "Defines whether the MEV searcher plugin is enabled",
            DefaultValue = "false")]
        bool Enabled { get; set; }
        
        [ConfigItem(
            Description = "Defines HTTP endpoint of the MEV relay, use https://relay.flashbots.net/ for mainnet and https://relay-goerli.flashbots.net/ for goerli",
            DefaultValue = "null")]
        string Endpoint { get; set; }
    }
}
