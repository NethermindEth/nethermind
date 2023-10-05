// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.EthStats
{
    public interface IEthStatsConfig : IConfig
    {
        [ConfigItem(Description = "If 'true' then EthStats publishing gets enabled.", DefaultValue = "false")]
        bool Enabled { get; }

        [ConfigItem(Description = "EthStats server wss://hostname:port/api/", DefaultValue = "ws://localhost:3000/api")]
        string? Server { get; }

        [ConfigItem(Description = "Node name displayed on the given ethstats server.", DefaultValue = "Nethermind")]
        string? Name { get; }

        [ConfigItem(Description = "Password for publishing to a given ethstats server.", DefaultValue = "secret")]
        string? Secret { get; }

        [ConfigItem(Description = "Node owner contact details displayed on the ethstats page.", DefaultValue = "hello@nethermind.io")]
        string? Contact { get; }
    }
}
