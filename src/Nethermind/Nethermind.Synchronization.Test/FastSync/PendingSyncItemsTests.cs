// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Reflection;
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
            using (Assert.EnterMultipleScope())
            {
                Assert.That(items.MaxStateLevel, Is.EqualTo(0));
                Assert.That(items.MaxStorageLevel, Is.EqualTo(0));
            }
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
            using (Assert.EnterMultipleScope())
            {
                Assert.That(items.Count, Is.EqualTo(0));
                Assert.That(batch[0].Level, Is.EqualTo(64));
                Assert.That(batch[1].Level, Is.EqualTo(32));
                Assert.That(batch[2].Level, Is.EqualTo(0));
            }
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
            using (Assert.EnterMultipleScope())
            {
                Assert.That(items.Count, Is.EqualTo(2));
                Assert.That(batch[0].NodeDataType, Is.EqualTo(NodeDataType.Code));
            }

            batch = items.TakeBatch(256);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(items.Count, Is.EqualTo(0));
                Assert.That(batch[0].NodeDataType, Is.EqualTo(NodeDataType.Storage));
                Assert.That(batch[1].NodeDataType, Is.EqualTo(NodeDataType.State));
            }
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
            using (Assert.EnterMultipleScope())
            {
                Assert.That(batch[0].Rightness, Is.EqualTo(0));
                Assert.That(batch[1].Rightness, Is.EqualTo(15));
                Assert.That(batch[2].Rightness, Is.EqualTo(10000));
            }
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
            using (Assert.EnterMultipleScope())
            {
                Assert.That(batch[0].Rightness, Is.EqualTo(0));
                Assert.That(batch[1].Rightness, Is.EqualTo(1));
                Assert.That(batch[2].Rightness, Is.EqualTo(15));
            }
        }

        // This trie type is four levels deep so far, the other one has seen a leaf: the deepest item is P0, the two
        // shallower ones P1. Measured against the other maximum, all three would be P1 and come out in reverse push
        // order.
        [TestCase(NodeDataType.Storage, 64, 0)]
        [TestCase(NodeDataType.State, 0, 64)]
        public void Depth_is_measured_against_the_maximum_of_its_own_trie(NodeDataType nodeDataType, byte maxStateLevel, byte maxStorageLevel)
        {
            IPendingSyncItems items = Init(isSnapSync: true); // no batch limit before the first state leaf
            items.MaxStateLevel = maxStateLevel;
            items.MaxStorageLevel = maxStorageLevel;

            StateSyncItem deep = PushItem(items, nodeDataType, 4, 0);
            StateSyncItem middle = PushItem(items, nodeDataType, 1, 0);
            StateSyncItem shallow = PushItem(items, nodeDataType, 0, 0);
            Assert.That(items.TakeBatch(256), Is.EqualTo(new[] { deep, shallow, middle }));
        }

        // A drained item of the other type leaves its rightness maximum at 10. Against this type's own maximum of 1000
        // the middle item is P1 and taken first; against 10, all three would be P2 and the last one taken first.
        [TestCase(NodeDataType.Storage, NodeDataType.State)]
        [TestCase(NodeDataType.State, NodeDataType.Storage)]
        public void Rightness_is_measured_against_the_maximum_of_its_own_trie(NodeDataType nodeDataType, NodeDataType otherType)
        {
            IPendingSyncItems items = Init(isSnapSync: true);
            items.MaxStateLevel = 64;
            items.MaxStorageLevel = 64;
            PushItem(items, otherType, 1, 10);
            items.TakeBatch(1);

            PushItem(items, nodeDataType, 1, 1000);
            StateSyncItem left = PushItem(items, nodeDataType, 1, 400);
            PushItem(items, nodeDataType, 1, 1000);
            Assert.That(items.TakeBatch(256)[0], Is.SameAs(left));
        }

        // The first seven rows fit in 32 bits and are unchanged; the last four wrapped before (old value in the comment).
        [TestCase(NodeType.Branch, 0, 0u, 15, 0xF000_0000u)]
        [TestCase(NodeType.Branch, 3, 0x1230_0000u, 2, 0x1232_0000u)]
        [TestCase(NodeType.Branch, 7, 0xFFFF_FFF0u, 15, 0xFFFF_FFFFu)]
        [TestCase(NodeType.Branch, 9, 100u, 3, 103u)]
        [TestCase(NodeType.Extension, 0, 0u, 0, 0xFFFF_FFFFu)]
        [TestCase(NodeType.Extension, 1, 0u, 0, 0x0FFF_FFFFu)]
        [TestCase(NodeType.Extension, 9, 100u, 0, 115u)]
        [TestCase(NodeType.Extension, 0, 5u, 0, uint.MaxValue)] // 4
        [TestCase(NodeType.Extension, 3, 0xFFFF_0000u, 0, uint.MaxValue)] // 0x000E_FFFF
        [TestCase(NodeType.Branch, 0, 0x9000_0000u, 8, uint.MaxValue)] // 0x1000_0000
        [TestCase(NodeType.Branch, 8, uint.MaxValue, 1, uint.MaxValue)] // 0
        public void Child_rightness_saturates(NodeType nodeType, int parentLevel, uint parentRightness, int childIndex, uint expected)
        {
            StateSyncItem parent = new(Keccak.Zero, null, TreePath.Empty, NodeDataType.Storage, parentLevel, parentRightness);
            Assert.That(CalculateRightness(nodeType, parent, childIndex), Is.EqualTo(expected));
        }

        private static readonly Func<NodeType, StateSyncItem, int, uint> CalculateRightness =
            typeof(TreeSync).GetMethod("CalculateRightness", BindingFlags.NonPublic | BindingFlags.Static)!
                .CreateDelegate<Func<NodeType, StateSyncItem, int, uint>>();

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
