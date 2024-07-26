// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Db.Blooms;

public interface IBloomConfig : IConfig
{
    [ConfigItem(Description = "Whether to use the Bloom index. The Bloom index speeds up the RPC log searches.", DefaultValue = "true")]
    bool Index { get; set; }

    [ConfigItem(Description = "An array of multipliers for index levels. Can be tweaked per chain to boost performance.", DefaultValue = "[4, 8, 8]")]
    int[] IndexLevelBucketSizes { get; set; }

    [ConfigItem(Description = "Whether the migration statistics should be calculated and output.", DefaultValue = "false")]
    bool MigrationStatistics { get; set; }

    [ConfigItem(Description = "Whether to migrate the previously downloaded blocks to the Bloom index.", DefaultValue = "false")]
    bool Migration { get; set; }
}
