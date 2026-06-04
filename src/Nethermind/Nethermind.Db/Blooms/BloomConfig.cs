// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Db.Blooms
{
    [Obsolete("BloomStorage has been replaced by the Log Index. This property is now a no-op.")]
    public class BloomConfig : IBloomConfig
    {
        [Obsolete("BloomStorage has been replaced by the Log Index. This property is now a no-op.")]
        public bool Index { get; set; } = true;

        [Obsolete("BloomStorage has been replaced by the Log Index. This property is now a no-op.")]
        public int[] IndexLevelBucketSizes { get; set; } = { 4, 8, 8 };

        [Obsolete("BloomStorage has been replaced by the Log Index. This property is now a no-op.")]
        public bool MigrationStatistics { get; set; } = false;

        [Obsolete("BloomStorage has been replaced by the Log Index. This property is now a no-op.")]
        public bool Migration { get; set; } = false;
    }
}
