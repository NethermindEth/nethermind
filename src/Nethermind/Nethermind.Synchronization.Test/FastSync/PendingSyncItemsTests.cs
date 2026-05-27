// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Synchronization.FastSync;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.FastSync
{
    [TestFixture]
    public class PendingSyncItemsTests
    {
        private IPendingSyncItems Init(bool isSnapSync = false) =>
            new PendingSyncItems(isSnapSync);

        [Test]
        public void At_start_count_is_zero()
        {
            IPendingSyncItems items = Init();
            Assert.That(items.Count, Is.EqualTo(0));
        }

        [Test]
        public void Description_does_not_throw_at_start()
        {
            IPendingSyncItems items = Init();
            Assert.That(string.IsNullOrWhiteSpace(items.Description), Is.False);
        }

        [Test]
        public void Max_levels_should_be_zero_at_start()
        {
            IPendingSyncItems items = Init();
            Assert.That(items.MaxStateLevel, Is.EqualTo(0));
            Assert.That(items.MaxStorageLevel, Is.EqualTo(0));
        }

        [Test]
        public void Can_recalculate_priorities_at_start()
        {
            IPendingSyncItems items = Init();
            Assert.That(string.IsNullOrWhiteSpace(items.RecalculatePriorities()), Is.False);
        }

        [Test]
        public void Peek_state_is_null_at_start()
        {
            IPendingSyncItems items = Init();
            Assert.That(items.PeekState(), Is.EqualTo(null));
        }

        [Test]
        public void Can_clear_at_start()
        {
            IPendingSyncItems items = Init();
            items.Clear();
        }

        [Test]
        public void Can_peek_root()
        {
            IPendingSyncItems items = Init();
            StateSyncItem stateSyncItem = new(Keccak.Zero, null, TreePath.Empty, NodeDataType.State);
            items.PushToSelectedStream(stateSyncItem, 0);
            Assert.That(items.PeekState(), Is.EqualTo(stateSyncItem));
        }

        [Test]
        public void Can_recalculate_and_clear_with_root_only()
        {
            IPendingSyncItems items = Init();
            StateSyncItem stateSyncItem = new(Keccak.Zero, null, TreePath.Empty, NodeDataType.State);
            items.PushToSelectedStream(stateSyncItem, 0);
            items.RecalculatePriorities();
            items.Clear();
            Assert.That(items.Count, Is.EqualTo(0));
        }

        [Test]
        public void Prioritizes_depth()
        {
            IPendingSyncItems items = Init();
            items.MaxStateLevel = 64;

            PushState(items, 0, 0);
            PushState(items, 32, 0);
            PushState(items, 64, 0);

            items.RecalculatePriorities();
            List<StateSyncItem> batch = items.TakeBatch(256);
            Assert.That(items.Count, Is.EqualTo(0));
            Assert.That(batch[0].Level, Is.EqualTo(64));
            Assert.That(batch[1].Level, Is.EqualTo(32));
            Assert.That(batch[2].Level, Is.EqualTo(0));
        }

        [Test]
        public void Limit_batch_at_start()
        {
            IPendingSyncItems items = Init();

            PushState(items, 0, 0);
            PushState(items, 32, 0);
            PushState(items, 64, 0);

            items.MaxStateLevel = 0;

            List<StateSyncItem> batch = items.TakeBatch(256);
            Assert.That(batch.Count, Is.EqualTo(1));

            items.MaxStateLevel = 64;

            batch = items.TakeBatch(256);
            Assert.That(batch.Count, Is.EqualTo(2));
        }

        [Test]
        public void DoNot_Limit_batch_at_start_if_snap_sync()
        {
            IPendingSyncItems items = Init(isSnapSync: true);

            PushState(items, 0, 0);
            PushState(items, 32, 0);
            PushState(items, 64, 0);

            items.MaxStateLevel = 0;

            List<StateSyncItem> batch = items.TakeBatch(256);
            Assert.That(batch.Count, Is.EqualTo(3));
        }

        [Test]
        public void Prioritizes_code_over_storage_over_state()
        {
            IPendingSyncItems items = Init();
            items.MaxStateLevel = 64;

            PushState(items, 64, 0);
            PushStorage(items, 32, 0);
            PushCode(items);

            items.RecalculatePriorities();
            List<StateSyncItem> batch = items.TakeBatch(256);
            Assert.That(items.Count, Is.EqualTo(2));
            Assert.That(batch[0].NodeDataType, Is.EqualTo(NodeDataType.Code));

            batch = items.TakeBatch(256);
            Assert.That(items.Count, Is.EqualTo(0));
            Assert.That(batch[0].NodeDataType, Is.EqualTo(NodeDataType.Storage));
            Assert.That(batch[1].NodeDataType, Is.EqualTo(NodeDataType.State));
        }

        [Test]
        public void Prefers_left()
        {
            IPendingSyncItems items = Init();
            items.MaxStateLevel = 64;

            PushState(items, 1, 10000); // something far-right
            PushState(items, 1, 15); // branch child 15
            PushState(items, 1, 0); // branch child 0

            items.RecalculatePriorities();
            List<StateSyncItem> batch = items.TakeBatch(256);
            Assert.That(batch[0].Rightness, Is.EqualTo(0));
            Assert.That(batch[1].Rightness, Is.EqualTo(15));
            Assert.That(batch[2].Rightness, Is.EqualTo(10000));
        }

        [Test]
        public void Prefers_left_single_branch()
        {
            IPendingSyncItems items = Init();
            items.MaxStateLevel = 64;

            PushState(items, 1, 15); // branch child 16
            PushState(items, 1, 1); // branch child 1
            PushState(items, 1, 0); // branch child 0

            items.RecalculatePriorities();
            List<StateSyncItem> batch = items.TakeBatch(256);
            Assert.That(batch[0].Rightness, Is.EqualTo(0));
            Assert.That(batch[1].Rightness, Is.EqualTo(1));
            Assert.That(batch[2].Rightness, Is.EqualTo(15));
        }

        private static StateSyncItem PushCode(IPendingSyncItems items, int progress = 0) =>
            PushItem(items, NodeDataType.Code, 0, 0, progress);

        private static StateSyncItem PushStorage(IPendingSyncItems items, int level, uint rightness, int progress = 0) =>
            PushItem(items, NodeDataType.Storage, level, rightness, progress);

        private static StateSyncItem PushState(IPendingSyncItems items, int level, uint rightness, int progress = 0) =>
            PushItem(items, NodeDataType.State, level, rightness, progress);

        private static StateSyncItem PushItem(IPendingSyncItems items, NodeDataType nodeDataType, int level, uint rightness, int progress = 0)
        {
            StateSyncItem stateSyncItem1 = new(Keccak.Zero, null, TreePath.Empty, nodeDataType, level, rightness);
            items.PushToSelectedStream(stateSyncItem1, progress);
            return stateSyncItem1;
        }
    }
}
