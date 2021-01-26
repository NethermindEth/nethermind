//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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
            SyncBatchSize syncBatchSize = new SyncBatchSize(LimboLogs.Instance);
            int beforeShrink = syncBatchSize.Current;
            syncBatchSize.Shrink();
            Assert.AreEqual(beforeShrink / 2, syncBatchSize.Current);
            int beforeExpand = syncBatchSize.Current;
            syncBatchSize.Expand();
            Assert.AreEqual(beforeExpand * 2, syncBatchSize.Current);
        }

        [Test]
        public void Cannot_go_below_min()
        {
            SyncBatchSize syncBatchSize = new SyncBatchSize(LimboLogs.Instance);
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
            SyncBatchSize syncBatchSize = new SyncBatchSize(LimboLogs.Instance);
            for (int i = 0; i < 100; i++)
            {
                syncBatchSize.Expand();
            }
            
            Assert.AreEqual(syncBatchSize.Current, SyncBatchSize.Max, "current is max");
            Assert.True(syncBatchSize.IsMax, "is max");
        }
    }
}
