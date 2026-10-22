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
    public void Default_parallelism_of_one_backfill_direction_leaves_cores_for_block_processing()
    {
        LogIndexConfig config = new();
        int total = config.MaxReceiptsParallelism + config.MaxAggregationParallelism + config.MaxCompressionParallelism;

        Assert.That(total, Is.LessThanOrEqualTo(Math.Max(Environment.ProcessorCount * 3 / 4, 3)));
    }
}
