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

namespace Nethermind.Seq.Config
{
    [ConfigCategory(Description = "Configuration of the Prometheus + Grafana metrics publication. Documentation of the required setup is not yet ready (but the metrics do work and are used by the dev team)")]
    public interface ISeqConfig : IConfig
    {
        [ConfigItem(Description = "Minimal level of log events which will be sent to Seq instance.", DefaultValue = "Off")]
        string MinLevel { get; }
        
        [ConfigItem(Description = "Seq instance URL.", DefaultValue = "\"http://localhost:5341")]
        string ServerUrl {get; }
        
        [ConfigItem(Description = "API key used for log events ingestion to Seq instance", DefaultValue = "")]
        string ApiKey {get; }
    }
}
