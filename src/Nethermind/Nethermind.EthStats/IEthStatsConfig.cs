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

namespace Nethermind.EthStats
{
    public interface IEthStatsConfig : IConfig
    {
        [ConfigItem(Description = "If 'true' then EthStats publishing gets enabled.", DefaultValue = "false")]
        bool Enabled { get; }
        
        [ConfigItem(Description = "EthStats server wss://hostname:port/api/", DefaultValue = "null")]
        string Server { get; }
        
        [ConfigItem(Description = "Node name displayed on the given ethstats server.", DefaultValue = "null")]
        string Name { get; }
        
        [ConfigItem(Description = "Password for publishing to a given ethstats server.", DefaultValue = "null")]
        string Secret { get; }
        
        [ConfigItem(Description = "Node owner contact details displayed on the ethstats page.", DefaultValue = "null")]
        string Contact { get; }
    }
}