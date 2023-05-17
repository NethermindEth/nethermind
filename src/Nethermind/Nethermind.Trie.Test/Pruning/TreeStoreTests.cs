// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.State.Witnesses;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Trie.Test.Pruning
{
    [TestFixture]
    public class TreeStoreTests
    {
        private readonly ILogManager _logManager = LimboLogs.Instance;
        // new OneLoggerLogManager(new NUnitLogger(LogLevel.Trace));

        [SetUp]
        public void Setup()
        {
        }

        [Test]
        public void Initial_memory_is_0()
        {
            using TrieStore trieStore = new(new MemDb(), new TestPruningStrategy(true), No.Persistence, _logManager);
            trieStore.MemoryUsedByDirtyCache.Should().Be(0);
        }

        [Test]
        public void Memory_with_one_node_is_288()
        {
            TrieNode trieNode = new(NodeType.Leaf, Keccak.Zero); // 56B

            using TrieStore trieStore = new(new MemDb(), new TestPruningStrategy(true), No.Persistence, _logManager);
            trieStore.CommitNode(1234, new NodeCommitInfo(trieNode));
            trieStore.MemoryUsedByDirtyCache.Should().Be(
                trieNode.GetMemorySize(false));
        }


        [Test]
        public void Pruning_off_cache_should_not_change_commit_node()
        {
            TrieNode trieNode = new(NodeType.Leaf, Keccak.Zero);
            TrieNode trieNode2 = new(NodeType.Branch, TestItem.KeccakA);
            TrieNode trieNode3 = new(NodeType.Branch, TestItem.KeccakB);

            using TrieStore trieStore = new(new MemDb(), No.Pruning, No.Persistence, _logManager);
            trieStore.CommitNode(1234, new NodeCommitInfo(trieNode));
            trieStore.FinishBlockCommit(TrieType.State, 1234, trieNode);
            trieStore.CommitNode(124, new NodeCommitInfo(trieNode2));
            trieStore.CommitNode(11234, new NodeCommitInfo(trieNode3));
            trieStore.MemoryUsedByDirtyCache.Should().Be(0);
        }

        [Test]
        public void When_commit_forward_write_flag_if_available()
        {
            TrieNode trieNode = new(NodeType.Leaf, Keccak.Zero);

            TestMemDb testMemDb = new TestMemDb();

            using TrieStore trieStore = new(testMemDb, No.Pruning, No.Persistence, _logManager);
            trieStore.CommitNode(1234, new NodeCommitInfo(trieNode), WriteFlags.LowPriority);
            trieStore.FinishBlockCommit(TrieType.State, 1234, trieNode, WriteFlags.LowPriority);
            testMemDb.KeyWasWrittenWithFlags(trieNode.Keccak.Bytes, WriteFlags.LowPriority);
        }

        [Test]
        public void Should_always_announce_block_number_when_pruning_disabled_and_persisting()
        {
            TrieNode trieNode = new(NodeType.Leaf, Keccak.Zero) { LastSeen = 1 };

            long reorgBoundaryCount = 0L;
            using TrieStore trieStore = new(new MemDb(), No.Pruning, Archive.Instance, _logManager);
            trieStore.ReorgBoundaryReached += (_, e) => reorgBoundaryCount += e.BlockNumber;
            trieStore.FinishBlockCommit(TrieType.State, 1, trieNode);
            reorgBoundaryCount.Should().Be(0);
            trieStore.FinishBlockCommit(TrieType.State, 2, trieNode);
            reorgBoundaryCount.Should().Be(1);
            trieStore.FinishBlockCommit(TrieType.State, 3, trieNode);
            reorgBoundaryCount.Should().Be(3);
            trieStore.FinishBlockCommit(TrieType.State, 4, trieNode);
            reorgBoundaryCount.Should().Be(6);
        }

        [Test]
        public void Should_always_announce_zero_when_not_persisting()
        {
            TrieNode trieNode = new(NodeType.Leaf, Keccak.Zero);

            long reorgBoundaryCount = 0L;
            using TrieStore trieStore = new(new MemDb(), No.Pruning, No.Persistence, _logManager);
            trieStore.ReorgBoundaryReached += (_, e) => reorgBoundaryCount += e.BlockNumber;
            trieStore.FinishBlockCommit(TrieType.State, 1, trieNode);
            trieStore.FinishBlockCommit(TrieType.State, 2, trieNode);
            trieStore.FinishBlockCommit(TrieType.State, 3, trieNode);
            trieStore.FinishBlockCommit(TrieType.State, 4, trieNode);
            reorgBoundaryCount.Should().Be(0L);
        }

        [Test]
        public void Pruning_off_cache_should_not_find_cached_or_unknown()
        {
            using TrieStore trieStore = new(new MemDb(), No.Pruning, No.Persistence, _logManager);
            TrieNode returnedNode = trieStore.FindCachedOrUnknown(TestItem.KeccakA);
            TrieNode returnedNode2 = trieStore.FindCachedOrUnknown(TestItem.KeccakB);
            TrieNode returnedNode3 = trieStore.FindCachedOrUnknown(TestItem.KeccakC);
            Assert.That(returnedNode.NodeType, Is.EqualTo(NodeType.Unknown));
            Assert.That(returnedNode2.NodeType, Is.EqualTo(NodeType.Unknown));
            Assert.That(returnedNode3.NodeType, Is.EqualTo(NodeType.Unknown));
            trieStore.MemoryUsedByDirtyCache.Should().Be(0);
        }

        [Test]
        public void FindCachedOrUnknown_CorrectlyCalculatedMemoryUsedByDirtyCache()
        {
            using TrieStore trieStore = new(new MemDb(), new TestPruningStrategy(true), No.Persistence, _logManager);
            long startSize = trieStore.MemoryUsedByDirtyCache;
            trieStore.FindCachedOrUnknown(TestItem.KeccakA);
            TrieNode trieNode = new(NodeType.Leaf, Keccak.Zero);
            long oneKeccakSize = trieNode.GetMemorySize(false);
            Assert.That(trieStore.MemoryUsedByDirtyCache, Is.EqualTo(startSize + oneKeccakSize));
            trieStore.FindCachedOrUnknown(TestItem.KeccakB);
            Assert.That(trieStore.MemoryUsedByDirtyCache, Is.EqualTo(2 * oneKeccakSize + startSize));
            trieStore.FindCachedOrUnknown(TestItem.KeccakB);
            Assert.That(trieStore.MemoryUsedByDirtyCache, Is.EqualTo(2 * oneKeccakSize + startSize));
            trieStore.FindCachedOrUnknown(TestItem.KeccakC);
            Assert.That(trieStore.MemoryUsedByDirtyCache, Is.EqualTo(3 * oneKeccakSize + startSize));
            trieStore.FindCachedOrUnknown(TestItem.KeccakD, true);
            Assert.That(trieStore.MemoryUsedByDirtyCache, Is.EqualTo(3 * oneKeccakSize + startSize));
        }

        [Test]
        public void Memory_with_two_nodes_is_correct()
        {
            TrieNode trieNode1 = new(NodeType.Leaf, TestItem.KeccakA);
            TrieNode trieNode2 = new(NodeType.Leaf, TestItem.KeccakB);

            using TrieStore trieStore = new(new MemDb(), new TestPruningStrategy(true), No.Persistence, _logManager);
            trieStore.CommitNode(1234, new NodeCommitInfo(trieNode1));
            trieStore.CommitNode(1234, new NodeCommitInfo(trieNode2));
            trieStore.MemoryUsedByDirtyCache.Should().Be(
                trieNode1.GetMemorySize(false) +
                trieNode2.GetMemorySize(false));
        }

        [Test]
        public void Memory_with_two_times_two_nodes_is_correct()
        {
            TrieNode trieNode1 = new(NodeType.Leaf, TestItem.KeccakA);
            TrieNode trieNode2 = new(NodeType.Leaf, TestItem.KeccakB);
            TrieNode trieNode3 = new(NodeType.Leaf, TestItem.KeccakA);
            TrieNode trieNode4 = new(NodeType.Leaf, TestItem.KeccakB);

            using TrieStore trieStore = new(new MemDb(), new TestPruningStrategy(true), No.Persistence, _logManager);
            trieStore.CommitNode(1234, new NodeCommitInfo(trieNode1));
            trieStore.CommitNode(1234, new NodeCommitInfo(trieNode2));
            trieStore.FinishBlockCommit(TrieType.State, 1234, trieNode2);
            trieStore.CommitNode(1235, new NodeCommitInfo(trieNode3));
            trieStore.CommitNode(1235, new NodeCommitInfo(trieNode4));

            // depending on whether the node gets resolved it gives different values here in debugging and run
            // needs some attention
            trieStore.MemoryUsedByDirtyCache.Should().BeLessOrEqualTo(
                trieNode1.GetMemorySize(false) +
                trieNode2.GetMemorySize(false));
        }

        [Test]
        public void Dispatcher_will_try_to_clear_memory()
        {
            TrieNode trieNode1 = new(NodeType.Leaf, new byte[0]);
            trieNode1.ResolveKey(null!, true);
            TrieNode trieNode2 = new(NodeType.Leaf, new byte[1]);
            trieNode2.ResolveKey(null!, true);

            TrieNode trieNode3 = new(NodeType.Leaf, new byte[2]);
            trieNode3.ResolveKey(null!, true);

            TrieNode trieNode4 = new(NodeType.Leaf, new byte[3]);
            trieNode4.ResolveKey(null!, true);

            using TrieStore trieStore = new(new MemDb(), new MemoryLimit(640), No.Persistence, _logManager);
            trieStore.CommitNode(1234, new NodeCommitInfo(trieNode1));
            trieStore.CommitNode(1234, new NodeCommitInfo(trieNode2));
            trieStore.FinishBlockCommit(TrieType.State, 1234, trieNode2);
            trieStore.CommitNode(1235, new NodeCommitInfo(trieNode3));
            trieStore.CommitNode(1235, new NodeCommitInfo(trieNode4));
            trieStore.FinishBlockCommit(TrieType.State, 1235, trieNode2);
            trieStore.FinishBlockCommit(TrieType.State, 1236, trieNode2);
            trieStore.MemoryUsedByDirtyCache.Should().Be(
                trieNode1.GetMemorySize(false) +
                trieNode2.GetMemorySize(false) +
                trieNode3.GetMemorySize(false) +
                trieNode4.GetMemorySize(false));
        }

        [Test]
        public void Dispatcher_will_try_to_clear_memory_the_soonest_possible()
        {
            TrieNode trieNode1 = new(NodeType.Leaf, new byte[0]);
            trieNode1.ResolveKey(null!, true);
            TrieNode trieNode2 = new(NodeType.Leaf, new byte[1]);
            trieNode2.ResolveKey(null!, true);

            TrieNode trieNode3 = new(NodeType.Leaf, new byte[2]);
            trieNode3.ResolveKey(null!, true);

            TrieNode trieNode4 = new(NodeType.Leaf, new byte[3]);
            trieNode4.ResolveKey(null!, true);

            using TrieStore trieStore = new(new MemDb(), new MemoryLimit(512), No.Persistence, _logManager);
            trieStore.CommitNode(1234, new NodeCommitInfo(trieNode1));
            trieStore.CommitNode(1234, new NodeCommitInfo(trieNode2));
            trieStore.FinishBlockCommit(TrieType.State, 1234, trieNode2);
            trieStore.CommitNode(1235, new NodeCommitInfo(trieNode3));
            trieStore.CommitNode(1235, new NodeCommitInfo(trieNode4));
            trieStore.MemoryUsedByDirtyCache.Should().Be(
                trieNode1.GetMemorySize(false) +
                trieNode2.GetMemorySize(false) +
                trieNode3.GetMemorySize(false) +
                trieNode4.GetMemorySize(false));
        }

        [Test]
        public void Dispatcher_will_always_try_to_clear_memory()
        {
            TrieStore trieStore = new(new MemDb(), new MemoryLimit(512), No.Persistence, _logManager);
            for (int i = 0; i < 1024; i++)
            {
                for (int j = 0; j < 1 + i % 3; j++)
                {
                    TrieNode trieNode = new(NodeType.Leaf, new byte[0]); // 192B
                    trieNode.ResolveKey(NullTrieNodeResolver.Instance, true);
                    trieStore.CommitNode(i, new NodeCommitInfo(trieNode));
                }

                TrieNode fakeRoot = new(NodeType.Leaf, new byte[0]); // 192B
                fakeRoot.ResolveKey(NullTrieNodeResolver.Instance, true);
                trieStore.FinishBlockCommit(TrieType.State, i, fakeRoot);
            }

            trieStore.MemoryUsedByDirtyCache.Should().BeLessThan(512 * 2);
        }

        [Test]
        public void Dispatcher_will_save_to_db_everything_from_snapshot_blocks()
        {
            TrieNode a = new(NodeType.Leaf, new byte[0]); // 192B
            a.ResolveKey(NullTrieNodeResolver.Instance, true);

            MemDb memDb = new();

            using TrieStore trieStore = new(memDb, new MemoryLimit(16.MB()), new ConstantInterval(4), _logManager);

            trieStore.CommitNode(0, new NodeCommitInfo(a));
            trieStore.FinishBlockCommit(TrieType.State, 0, a);
            trieStore.FinishBlockCommit(TrieType.State, 1, a);
            trieStore.FinishBlockCommit(TrieType.State, 2, a);
            trieStore.FinishBlockCommit(TrieType.State, 3, a);
            trieStore.FinishBlockCommit(TrieType.State, 4, a);

            memDb[a.Keccak!.Bytes].Should().NotBeNull();
            trieStore.IsNodeCached(a.Keccak).Should().BeTrue();
        }

        [Test]
        public void Stays_in_memory_until_persisted()
        {
            TrieNode a = new(NodeType.Leaf, new byte[0]); // 192B
            a.ResolveKey(NullTrieNodeResolver.Instance, true);

            MemDb memDb = new();

            using TrieStore trieStore = new(memDb, new MemoryLimit(16.MB()), No.Persistence, _logManager);

            trieStore.CommitNode(0, new NodeCommitInfo(a));
            trieStore.FinishBlockCommit(TrieType.State, 0, a);
            trieStore.FinishBlockCommit(TrieType.State, 1, a);
            trieStore.FinishBlockCommit(TrieType.State, 2, a);
            trieStore.FinishBlockCommit(TrieType.State, 3, a);
            //  <- do not persist in this test

            memDb[a.Keccak!.Bytes].Should().BeNull();
            trieStore.IsNodeCached(a.Keccak).Should().BeTrue();
        }

        [Test]
        public void Can_load_from_rlp()
        {
            MemDb memDb = new();
            memDb[Keccak.Zero.Bytes] = new byte[] { 1, 2, 3 };

            using TrieStore trieStore = new(memDb, _logManager);
            trieStore.LoadRlp(Keccak.Zero).Should().NotBeNull();
        }

        [Test]
        public void Will_get_persisted_on_snapshot_if_referenced()
        {
            TrieNode a = new(NodeType.Leaf, new byte[0]); // 192B
            a.ResolveKey(NullTrieNodeResolver.Instance, true);

            MemDb memDb = new();

            using TrieStore trieStore = new(memDb, new MemoryLimit(16.MB()), new ConstantInterval(4), _logManager);

            trieStore.FinishBlockCommit(TrieType.State, 0, null);
            trieStore.CommitNode(1, new NodeCommitInfo(a));
            trieStore.FinishBlockCommit(TrieType.State, 1, a);
            trieStore.FinishBlockCommit(TrieType.State, 2, a);
            trieStore.FinishBlockCommit(TrieType.State, 3, a);
            trieStore.FinishBlockCommit(TrieType.State, 4, a);
            trieStore.FinishBlockCommit(TrieType.State, 5, a);
            trieStore.FinishBlockCommit(TrieType.State, 6, a);
            trieStore.FinishBlockCommit(TrieType.State, 7, a);
            trieStore.FinishBlockCommit(TrieType.State, 8, a);

            memDb[a.Keccak!.Bytes].Should().NotBeNull();
            trieStore.IsNodeCached(a.Keccak).Should().BeTrue();
        }

        [Test]
        public void Will_not_get_dropped_on_snapshot_if_unreferenced_in_later_blocks()
        {
            TrieNode a = new(NodeType.Leaf, new byte[0]);
            a.ResolveKey(NullTrieNodeResolver.Instance, true);

            TrieNode b = new(NodeType.Leaf, new byte[1]);
            b.ResolveKey(NullTrieNodeResolver.Instance, true);

            MemDb memDb = new();

            using TrieStore trieStore = new(memDb, new MemoryLimit(16.MB()), new ConstantInterval(4), _logManager);

            trieStore.FinishBlockCommit(TrieType.State, 0, null);
            trieStore.CommitNode(1, new NodeCommitInfo(a));
            trieStore.FinishBlockCommit(TrieType.State, 1, a);
            trieStore.FinishBlockCommit(TrieType.State, 2, a);
            trieStore.FinishBlockCommit(TrieType.State, 3, a);
            trieStore.FinishBlockCommit(TrieType.State, 4, a);
            trieStore.FinishBlockCommit(TrieType.State, 5, a);
            trieStore.FinishBlockCommit(TrieType.State, 6, a);
            trieStore.CommitNode(7, new NodeCommitInfo(b));
            trieStore.FinishBlockCommit(TrieType.State, 7, b);
            trieStore.FinishBlockCommit(TrieType.State, 8, b);

            memDb[a.Keccak!.Bytes].Should().NotBeNull();
            trieStore.IsNodeCached(a.Keccak).Should().BeTrue();
        }

        [Test]
        public void Will_get_dropped_on_snapshot_if_it_was_a_transient_node()
        {
            TrieNode a = new(NodeType.Leaf, new byte[] { 1 });
            a.ResolveKey(NullTrieNodeResolver.Instance, true);

            TrieNode b = new(NodeType.Leaf, new byte[] { 2 });
            b.ResolveKey(NullTrieNodeResolver.Instance, true);

            MemDb memDb = new();

            using TrieStore trieStore = new(memDb, new MemoryLimit(16.MB()), new ConstantInterval(4), _logManager);

            trieStore.FinishBlockCommit(TrieType.State, 0, null);
            trieStore.CommitNode(1, new NodeCommitInfo(a));
            trieStore.FinishBlockCommit(TrieType.State, 1, a);
            trieStore.FinishBlockCommit(TrieType.State, 2, a);
            trieStore.CommitNode(3, new NodeCommitInfo(b)); // <- new root
            trieStore.FinishBlockCommit(TrieType.State, 3, b);
            trieStore.FinishBlockCommit(TrieType.State, 4, b); // should be 'a' to test properly
            trieStore.FinishBlockCommit(TrieType.State, 5, b); // should be 'a' to test properly
            trieStore.FinishBlockCommit(TrieType.State, 6, b); // should be 'a' to test properly
            trieStore.FinishBlockCommit(TrieType.State, 7, b); // should be 'a' to test properly
            trieStore.FinishBlockCommit(TrieType.State, 8, b); // should be 'a' to test properly

            memDb[a.Keccak!.Bytes].Should().BeNull();
            trieStore.IsNodeCached(a.Keccak).Should().BeTrue();
        }

        private class BadDb : IKeyValueStoreWithBatching
        {
            private Dictionary<byte[], byte[]> _db = new();

            public byte[]? this[ReadOnlySpan<byte> key]
            {
                get => Get(key);
                set => Set(key, value);
            }

            public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
            {
                _db[key.ToArray()] = value;
            }

            public byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
            {
                return _db[key.ToArray()];
            }

            public IBatch StartBatch()
            {
                return new BadBatch();
            }

            private class BadBatch : IBatch
            {
                private Dictionary<byte[], byte[]> _inBatched = new();

                public void Dispose()
                {
                }

                public byte[]? this[ReadOnlySpan<byte> key]
                {
                    get => Get(key);
                    set => Set(key, value);
                }

                public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
                {
                    _inBatched[key.ToArray()] = value;
                }

                public byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
                {
                    return _inBatched[key.ToArray()];
                }
            }
        }


        [Test]
        public void Trie_store_multi_threaded_scenario()
        {
            using TrieStore trieStore = new(new BadDb(), _logManager);
            StateTree tree = new(trieStore, _logManager);
            tree.Set(TestItem.AddressA, Build.A.Account.WithBalance(1000).TestObject);
            tree.Set(TestItem.AddressB, Build.A.Account.WithBalance(1000).TestObject);
        }

        private AccountDecoder _accountDecoder = new();

        [Test]
        public void Will_store_storage_on_snapshot()
        {
            TrieNode storage1 = new(NodeType.Leaf, new byte[2]);
            storage1.ResolveKey(NullTrieNodeResolver.Instance, true);

            TrieNode a = new(NodeType.Leaf);
            Account account = new(1, 1, storage1.Keccak, Keccak.OfAnEmptyString);
            a.Value = _accountDecoder.Encode(account).Bytes;
            a.Key = Bytes.FromHexString("abc");
            a.ResolveKey(NullTrieNodeResolver.Instance, true);

            MemDb memDb = new();

            using TrieStore trieStore = new(memDb, new MemoryLimit(16.MB()), new ConstantInterval(4), _logManager);
            trieStore.FinishBlockCommit(TrieType.State, 0, null);
            trieStore.CommitNode(1, new NodeCommitInfo(a));
            trieStore.CommitNode(1, new NodeCommitInfo(storage1));
            trieStore.FinishBlockCommit(TrieType.Storage, 1, storage1);
            trieStore.FinishBlockCommit(TrieType.State, 1, a);
            trieStore.FinishBlockCommit(TrieType.State, 2, a);
            trieStore.FinishBlockCommit(TrieType.State, 3, a);
            trieStore.FinishBlockCommit(TrieType.State, 4, a);
            trieStore.FinishBlockCommit(TrieType.State, 5, a);
            trieStore.FinishBlockCommit(TrieType.State, 6, a);
            trieStore.FinishBlockCommit(TrieType.State, 7, a);
            trieStore.FinishBlockCommit(TrieType.State, 8, a);

            memDb[a.Keccak!.Bytes].Should().NotBeNull();
            memDb[storage1.Keccak!.Bytes].Should().NotBeNull();
            trieStore.IsNodeCached(a.Keccak).Should().BeTrue();
            // trieStore.IsInMemory(storage1.Keccak).Should().BeFalse();
        }

        [Test]
        public void Will_drop_transient_storage()
        {
            TrieNode storage1 = new(NodeType.Leaf, new byte[2]);
            storage1.ResolveKey(NullTrieNodeResolver.Instance, true);

            TrieNode a = new(NodeType.Leaf);
            Account account = new(1, 1, storage1.Keccak, Keccak.OfAnEmptyString);
            a.Value = _accountDecoder.Encode(account).Bytes;
            a.Key = Bytes.FromHexString("abc");
            a.ResolveKey(NullTrieNodeResolver.Instance, true);

            TrieNode b = new(NodeType.Leaf, new byte[1]);
            b.ResolveKey(NullTrieNodeResolver.Instance, true);

            MemDb memDb = new();

            using TrieStore trieStore = new(memDb, new MemoryLimit(16.MB()), new ConstantInterval(4), _logManager);

            trieStore.FinishBlockCommit(TrieType.State, 0, null);
            trieStore.CommitNode(1, new NodeCommitInfo(a));
            trieStore.CommitNode(1, new NodeCommitInfo(storage1));
            trieStore.FinishBlockCommit(TrieType.Storage, 1, storage1);
            trieStore.FinishBlockCommit(TrieType.State, 1, a);
            trieStore.FinishBlockCommit(TrieType.State, 2, a);
            trieStore.CommitNode(3, new NodeCommitInfo(b)); // <- new root
            trieStore.FinishBlockCommit(TrieType.State, 3, b);
            trieStore.FinishBlockCommit(TrieType.State, 4, b); // should be 'a' to test properly
            trieStore.FinishBlockCommit(TrieType.State, 5, b); // should be 'a' to test properly
            trieStore.FinishBlockCommit(TrieType.State, 6, b); // should be 'a' to test properly
            trieStore.FinishBlockCommit(TrieType.State, 7, b); // should be 'a' to test properly
            trieStore.FinishBlockCommit(TrieType.State, 8, b); // should be 'a' to test properly

            memDb[a.Keccak!.Bytes].Should().BeNull();
            memDb[storage1.Keccak!.Bytes].Should().BeNull();
            trieStore.IsNodeCached(a.Keccak).Should().BeTrue();
            trieStore.IsNodeCached(storage1.Keccak).Should().BeTrue();
        }

        [Test]
        public void Will_combine_same_storage()
        {
            TrieNode storage1 = new(NodeType.Leaf, new byte[32]);
            storage1.ResolveKey(NullTrieNodeResolver.Instance, true);

            TrieNode a = new(NodeType.Leaf);
            Account account = new(1, 1, storage1.Keccak, Keccak.OfAnEmptyString);
            a.Value = _accountDecoder.Encode(account).Bytes;
            a.Key = Bytes.FromHexString("abc");
            a.ResolveKey(NullTrieNodeResolver.Instance, true);

            TrieNode storage2 = new(NodeType.Leaf, new byte[32]);
            storage2.ResolveKey(NullTrieNodeResolver.Instance, true);

            TrieNode b = new(NodeType.Leaf);
            Account accountB = new(2, 1, storage2.Keccak, Keccak.OfAnEmptyString);
            b.Value = _accountDecoder.Encode(accountB).Bytes;
            b.Key = Bytes.FromHexString("abcd");
            b.ResolveKey(NullTrieNodeResolver.Instance, true);

            TrieNode branch = new(NodeType.Branch);
            branch.SetChild(0, a);
            branch.SetChild(1, b);
            branch.ResolveKey(NullTrieStore.Instance, true);

            MemDb memDb = new();

            using TrieStore trieStore = new(memDb, new MemoryLimit(16.MB()), new ConstantInterval(4), _logManager);

            trieStore.FinishBlockCommit(TrieType.State, 0, null);
            trieStore.CommitNode(1, new NodeCommitInfo(storage1));
            trieStore.FinishBlockCommit(TrieType.Storage, 1, storage1);
            trieStore.CommitNode(1, new NodeCommitInfo(storage2));
            trieStore.FinishBlockCommit(TrieType.Storage, 1, storage2);
            trieStore.CommitNode(1, new NodeCommitInfo(a));
            trieStore.CommitNode(1, new NodeCommitInfo(b));
            trieStore.CommitNode(1, new NodeCommitInfo(branch));
            trieStore.FinishBlockCommit(TrieType.State, 1, branch);
            trieStore.FinishBlockCommit(TrieType.State, 2, branch);
            trieStore.FinishBlockCommit(TrieType.State, 3, branch);
            trieStore.FinishBlockCommit(TrieType.State, 4, branch);
            trieStore.FinishBlockCommit(TrieType.State, 5, branch);
            trieStore.FinishBlockCommit(TrieType.State, 6, branch);
            trieStore.FinishBlockCommit(TrieType.State, 7, branch);
            trieStore.FinishBlockCommit(TrieType.State, 8, branch);

            memDb[a.Keccak!.Bytes].Should().NotBeNull();
            memDb[storage1.Keccak!.Bytes].Should().NotBeNull();
            trieStore.IsNodeCached(a.Keccak).Should().BeTrue();
            trieStore.IsNodeCached(storage1.Keccak).Should().BeTrue();
        }

        [Test]
        public void ReadOnly_store_doesnt_change_witness()
        {
            TrieNode node = new(NodeType.Leaf);
            Account account = new(1, 1, TestItem.KeccakA, Keccak.OfAnEmptyString);
            node.Value = _accountDecoder.Encode(account).Bytes;
            node.Key = Bytes.FromHexString("abc");
            node.ResolveKey(NullTrieNodeResolver.Instance, true);

            MemDb originalStore = new MemDb();
            WitnessCollector witnessCollector = new WitnessCollector(new MemDb(), LimboLogs.Instance);
            IKeyValueStoreWithBatching store = originalStore.WitnessedBy(witnessCollector);
            using TrieStore trieStore = new(store, new TestPruningStrategy(false), No.Persistence, _logManager);
            trieStore.CommitNode(0, new NodeCommitInfo(node));
            trieStore.FinishBlockCommit(TrieType.State, 0, node);

            IReadOnlyTrieStore readOnlyTrieStore = trieStore.AsReadOnly(originalStore);
            readOnlyTrieStore.LoadRlp(node.Keccak);

            witnessCollector.Collected.Should().BeEmpty();
        }

        [TestCase(true)]
        [TestCase(false, Explicit = true)]
        public async Task Read_only_trie_store_is_allowing_many_thread_to_work_with_the_same_node(bool beThreadSafe)
        {
            TrieNode trieNode = new(NodeType.Branch);
            for (int i = 0; i < 16; i++)
            {
                trieNode.SetChild(i, new TrieNode(NodeType.Unknown, TestItem.Keccaks[i]));
            }

            trieNode.Seal();

            MemDb memDb = new();
            using TrieStore store = new(memDb, Prune.WhenCacheReaches(10.MB()), Persist.IfBlockOlderThan(10), _logManager);
            ITrieStore trieStore = store;
            trieNode.ResolveKey(trieStore, false);
            trieStore.CommitNode(1, new NodeCommitInfo(trieNode));

            if (beThreadSafe)
            {
                trieStore = trieStore.AsReadOnly(memDb);
            }

            void CheckChildren()
            {
                for (int i = 0; i < 16 * 10; i++)
                {
                    try
                    {
                        trieStore.FindCachedOrUnknown(trieNode.Keccak).GetChildHash(i % 16).Should().BeEquivalentTo(TestItem.Keccaks[i % 16], i.ToString());
                    }
                    catch (Exception)
                    {
                        throw new AssertionException("Failed");
                    }
                }
            }

            List<Task> tasks = new();
            for (int i = 0; i < 2; i++)
            {
                Task task = new(CheckChildren);
                task.Start();
                tasks.Add(task);
            }

            if (beThreadSafe)
            {
                await Task.WhenAll();
            }
            else
            {
                Assert.ThrowsAsync<AssertionException>(() => Task.WhenAll(tasks));
            }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ReadOnly_store_returns_copies(bool pruning)
        {
            TrieNode node = new(NodeType.Leaf);
            Account account = new(1, 1, TestItem.KeccakA, Keccak.OfAnEmptyString);
            node.Value = _accountDecoder.Encode(account).Bytes;
            node.Key = Bytes.FromHexString("abc");
            node.ResolveKey(NullTrieNodeResolver.Instance, true);

            using TrieStore trieStore = new(new MemDb(), new TestPruningStrategy(pruning), No.Persistence, _logManager);
            trieStore.CommitNode(0, new NodeCommitInfo(node));
            trieStore.FinishBlockCommit(TrieType.State, 0, node);
            var originalNode = trieStore.FindCachedOrUnknown(node.Keccak);

            IReadOnlyTrieStore readOnlyTrieStore = trieStore.AsReadOnly();
            var readOnlyNode = readOnlyTrieStore.FindCachedOrUnknown(node.Keccak);

            readOnlyNode.Should().NotBe(originalNode);
            readOnlyNode.Should().BeEquivalentTo(originalNode,
                eq => eq.Including(t => t.Keccak)
                    .Including(t => t.FullRlp)
                    .Including(t => t.NodeType));

            readOnlyNode.Key?.ToString().Should().Be(originalNode.Key?.ToString());
        }
    }
}
