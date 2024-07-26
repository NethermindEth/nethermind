// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.EthStats;

public interface IEthStatsConfig : IConfig
{
    [ConfigItem(Description = "Whether to use Ethstats publishing.", DefaultValue = "false")]
    bool Enabled { get; }

    [ConfigItem(Description = "The Ethstats server URL.", DefaultValue = "ws://localhost:3000/api")]
    string? Server { get; }

    [ConfigItem(Description = "The node name displayed on Ethstats.", DefaultValue = "Nethermind")]
    string? Name { get; }

    [ConfigItem(Description = "The Ethstats secret.", DefaultValue = "secret")]
    string? Secret { get; }

    [ConfigItem(Description = "The node owner contact details displayed on Ethstats.", DefaultValue = "hello@nethermind.io")]
    string? Contact { get; }

    [ConfigItem(Description = "The stats update interval, in seconds.", DefaultValue = "15")]
    int SendInterval { get; }
}
