// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Logging;
using Nethermind.Synchronization.Blocks;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class SyncBatchSizeTests
    {
        [Test]
        public void Can_shrink_and_expand()
        {
            SyncBatchSize syncBatchSize = new(LimboLogs.Instance);
            int beforeShrink = syncBatchSize.Current;
            syncBatchSize.Shrink();
            Assert.AreEqual(Math.Floor(beforeShrink / SyncBatchSize.AdjustmentFactor), syncBatchSize.Current);
            int beforeExpand = syncBatchSize.Current;
            syncBatchSize.Expand();
            Assert.AreEqual(Math.Ceiling(beforeExpand * SyncBatchSize.AdjustmentFactor), syncBatchSize.Current);
        }

        [Test]
        public void Cannot_go_below_min()
        {
            SyncBatchSize syncBatchSize = new(LimboLogs.Instance);
            for (int i = 0; i < 100; i++)
            {
                syncBatchSize.Shrink();
            }

            Assert.AreEqual(syncBatchSize.Current, SyncBatchSize.Min, "current is min");
            Assert.True(syncBatchSize.IsMin, "is min");
        }

        [Test]
        public void Cannot_go_above_max()
        {
            SyncBatchSize syncBatchSize = new(LimboLogs.Instance);
            for (int i = 0; i < 100; i++)
            {
                syncBatchSize.Expand();
            }

            Assert.AreEqual(syncBatchSize.Current, SyncBatchSize.Max, "current is max");
            Assert.True(syncBatchSize.IsMax, "is max");
        }
    }
}
