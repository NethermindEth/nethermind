//  Copyright (c) 2018 Demerzel Solutions Limited
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
// 

using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Synchronization.FastSync;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.FastSync
{
    [TestFixture]
    public class PendingSyncItemsTests
    {
        private StateSyncFeed.IPendingSyncItems Init()
        {
            return new StateSyncFeed.PendingSyncItems2();
        }
        
        [Test]
        public void At_start_count_is_zero()
        {
            StateSyncFeed.IPendingSyncItems items = Init();
            items.Count.Should().Be(0);
        }

        [Test]
        public void Description_does_not_throw_at_start()
        {
            StateSyncFeed.IPendingSyncItems items = Init();
            items.Description.Should().NotBeNullOrWhiteSpace();
        }

        [Test]
        public void Max_levels_should_be_zero_at_start()
        {
            StateSyncFeed.IPendingSyncItems items = Init();
            items.MaxStateLevel.Should().Be(0);
            items.MaxStorageLevel.Should().Be(0);
        }

        [Test]
        public void Can_recalculate_priorities_at_start()
        {
            StateSyncFeed.IPendingSyncItems items = Init();
            items.RecalculatePriorities().Should().NotBeNullOrWhiteSpace();
        }

        [Test]
        public void Peek_state_is_null_at_start()
        {
            StateSyncFeed.IPendingSyncItems items = Init();
            items.PeekState().Should().Be(null);
        }

        [Test]
        public void Can_clear_at_start()
        {
            StateSyncFeed.IPendingSyncItems items = Init();
            items.Clear();
        }

        [Test]
        public void Can_peek_root()
        {
            StateSyncFeed.IPendingSyncItems items = Init();
            StateSyncItem stateSyncItem = new StateSyncItem(Keccak.Zero, NodeDataType.State, 0, 0);
            items.PushToSelectedStream(stateSyncItem, 0);
            items.PeekState().Should().Be(stateSyncItem);
        }

        [Test]
        public void Can_recalculate_and_clear_with_root_only()
        {
            StateSyncFeed.IPendingSyncItems items = Init();
            StateSyncItem stateSyncItem = new StateSyncItem(Keccak.Zero, NodeDataType.State, 0, 0);
            items.PushToSelectedStream(stateSyncItem, 0);
            items.RecalculatePriorities();
            items.Clear();
            items.Count.Should().Be(0);
        }

        [Test]
        public void Prioritizes_depth()
        {
            StateSyncFeed.IPendingSyncItems items = Init();
            items.MaxStateLevel = 64;

            PushState(items, 0, 0);
            PushState(items, 32, 0);
            PushState(items, 64, 0);

            items.RecalculatePriorities();
            List<StateSyncItem> batch = items.TakeBatch(256);
            items.Count.Should().Be(0);
            batch[0].Level.Should().Be(64);
            batch[1].Level.Should().Be(32);
            batch[2].Level.Should().Be(0);
        }

        [Test]
        public void Prioritizes_code_over_storage_over_state()
        {
            StateSyncFeed.IPendingSyncItems items = Init();
            items.MaxStateLevel = 64;

            PushState(items, 64, 0);
            PushStorage(items, 32, 0);
            PushCode(items);

            items.RecalculatePriorities();
            List<StateSyncItem> batch = items.TakeBatch(256);
            items.Count.Should().Be(0);
            batch[0].NodeDataType.Should().Be(NodeDataType.Code);
            batch[1].NodeDataType.Should().Be(NodeDataType.Storage);
            batch[2].NodeDataType.Should().Be(NodeDataType.State);
        }
        
        [Test]
        public void Prefers_left()
        {
            StateSyncFeed.IPendingSyncItems items = Init();
            items.MaxStateLevel = 64;

            PushState(items, 1, 10000); // something far-right
            PushState(items, 1, 15); // branch child 15
            PushState(items, 1, 0); // branch child 0

            items.RecalculatePriorities();
            List<StateSyncItem> batch = items.TakeBatch(256);
            batch[0].Rightness.Should().Be(0);
            batch[1].Rightness.Should().Be(15);
            batch[2].Rightness.Should().Be(10000);
        }
        
        [Test]
        public void Prefers_left_single_branch()
        {
            StateSyncFeed.IPendingSyncItems items = Init();
            items.MaxStateLevel = 64;

            PushState(items, 1, 15); // branch child 16
            PushState(items, 1, 1); // branch child 1
            PushState(items, 1, 0); // branch child 0

            items.RecalculatePriorities();
            List<StateSyncItem> batch = items.TakeBatch(256);
            batch[0].Rightness.Should().Be(0);
            batch[1].Rightness.Should().Be(1);
            batch[2].Rightness.Should().Be(15);
        }

        private static StateSyncItem PushCode(StateSyncFeed.IPendingSyncItems items, int progress = 0)
        {
            return PushItem(items, NodeDataType.Code, 0, 0, progress);
        }

        private static StateSyncItem PushStorage(StateSyncFeed.IPendingSyncItems items, int level, uint rightness, int progress = 0)
        {
            return PushItem(items, NodeDataType.Storage, level, rightness, progress);
        }

        private static StateSyncItem PushState(StateSyncFeed.IPendingSyncItems items, int level, uint rightness, int progress = 0)
        {
            return PushItem(items, NodeDataType.State, level, rightness, progress);
        }

        private static StateSyncItem PushItem(StateSyncFeed.IPendingSyncItems items, NodeDataType nodeDataType, int level, uint rightness, int progress = 0)
        {
            StateSyncItem stateSyncItem1 = new StateSyncItem(Keccak.Zero, nodeDataType, level, rightness);
            items.PushToSelectedStream(stateSyncItem1, progress);
            return stateSyncItem1;
        }
    }
}