// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Db.Blooms
{
    /// <remarks>
    /// See <see cref="Nethermind.Db.ILogIndexConfig"/> for the replacement.
    /// </remarks>
    [Obsolete("BloomStorage has been replaced by the Log Index. This property is now a no-op.")]
    public class BloomConfig : IBloomConfig
    {
        [Obsolete("BloomStorage has been replaced by the Log Index. This property is now a no-op.")]
        public bool Index { get; set; }

        [Obsolete("BloomStorage has been replaced by the Log Index. This property is now a no-op.")]
        public int[] IndexLevelBucketSizes { get; set; }

        [Obsolete("BloomStorage has been replaced by the Log Index. This property is now a no-op.")]
        public bool MigrationStatistics { get; set; }

        [Obsolete("BloomStorage has been replaced by the Log Index. This property is now a no-op.")]
        public bool Migration { get; set; }
    }
}
