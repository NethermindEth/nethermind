// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Db.Blooms
{
    public interface IBloomConfig : IConfig
    {
        [ConfigItem(Description = "Defines whether the Bloom index is used. Bloom index speeds up rpc log searches.", DefaultValue = "true")]
        bool Index { get; set; }

        [ConfigItem(Description = "Defines multipliers for index levels. Can be tweaked per chain to boost performance.", DefaultValue = "[4, 8, 8]")]
        int[] IndexLevelBucketSizes { get; set; }

        [ConfigItem(Description = "Defines if migration statistics are to be calculated and output.", DefaultValue = "false")]
        bool MigrationStatistics { get; set; }

        [ConfigItem(Description = "Defines if migration of previously downloaded blocks to Bloom index will be done.", DefaultValue = "false")]
        bool Migration { get; set; }
    }
}
