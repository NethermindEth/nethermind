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

namespace Nethermind.Monitoring.Config
{
    [ConfigCategory(Description = "Configuration of the Prometheus + Grafana metrics publication. Documentation of the required setup is not yet ready (but the metrics do work and are used by the dev team)")]
    public interface IMetricsConfig : IConfig
    {
        [ConfigItem(Description = "If 'true' then the node publishes various metrics to Prometheus at the given interval.", DefaultValue = "false")]
        bool Enabled { get; }
        
        [ConfigItem(Description = "Prometheus URL.", DefaultValue = "\"http://localhost:9091/metrics\"")]
        string PushGatewayUrl {get; }
        
        [ConfigItem(DefaultValue = "5")]
        int IntervalSeconds {get; }
        
        [ConfigItem(Description = "Name displayed in the Grafana dashboard", DefaultValue = "\"Nethermind\"")]
        string NodeName { get; }
    }
}