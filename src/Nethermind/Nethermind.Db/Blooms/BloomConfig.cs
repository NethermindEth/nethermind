// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Db.Blooms
{
    public class BloomConfig : IBloomConfig
    {
        public bool Index { get; set; } = true;

        public int[] IndexLevelBucketSizes { get; set; } = { 4, 8, 8 };

        public bool MigrationStatistics { get; set; } = false;

        public bool Migration { get; set; } = false;
    }
}
