// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Db.LogIndex;
using NUnit.Framework;

namespace Nethermind.Db.Test.LogIndex;

[Parallelizable(ParallelScope.All)]
public class LogIndexConfigTests
{
    [Test]
    public void Default_parallelism_of_both_backfill_directions_stays_near_core_count()
    {
        LogIndexConfig config = new();
        // Each direction has its own receipts and aggregation workers; the compressor is shared.
        int peak = 2 * (config.MaxReceiptsParallelism + config.MaxAggregationParallelism) + config.MaxCompressionParallelism;

        Assert.That(peak, Is.LessThanOrEqualTo(Math.Max(Environment.ProcessorCount * 5 / 4, 5)));
    }
}
