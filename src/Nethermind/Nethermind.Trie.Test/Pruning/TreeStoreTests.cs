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
    [TestFixture(INodeStorage.KeyScheme.HalfPath)]
    [TestFixture(INodeStorage.KeyScheme.Hash)]
    public class TreeStoreTests
    {
        private readonly ILogManager _logManager = LimboLogs.Instance;
        // new OneLoggerLogManager(new NUnitLogger(LogLevel.Trace));

        private readonly AccountDecoder _accountDecoder = new();
        private readonly INodeStorage.KeyScheme _scheme;

        public TreeStoreTests(INodeStorage.KeyScheme scheme)
        {
            _scheme = scheme;
        }

        private TrieStore CreateTrieStore(
            IPruningStrategy? pruningStrategy = null,
            IKeyValueStoreWithBatching? kvStore = null,
            IPersistenceStrategy? persistenceStrategy = null,
            long? reorgDepthOverride = null
        )
        {
            pruningStrategy ??= No.Pruning;
            kvStore ??= new TestMemDb();
            persistenceStrategy ??= No.Persistence;
            return new(
                new NodeStorage(kvStore, _scheme, requirePath: _scheme == INodeStorage.KeyScheme.HalfPath),
                pruningStrategy,
                persistenceStrategy,
                _logManager,
                reorgDepthOverride: reorgDepthOverride);
        }

        [SetUp]
        public void Setup()
        {
        }

        [Test]
        public void Initial_memory_is_0()
        {
            using TrieStore trieStore = CreateTrieStore(pruningStrategy: new TestPruningStrategy(true));
            trieStore.MemoryUsedByDirtyCache.Should().Be(0);
        }

        [Test]
        public void Memory_with_one_node_is_288()
        {
            TrieNode trieNode = new(NodeType.Leaf, Keccak.Zero); // 56B

            using TrieStore fullTrieStore = CreateTrieStore(pruningStrategy: new TestPruningStrategy(true));
            IScopedTrieStore trieStore = fullTrieStore.GetTrieStore(null);
            trieStore.CommitNode(1234, new NodeCommitInfo(trieNode, TreePath.Empty));
            fullTrieStore.MemoryUsedByDirtyCache.Should().Be(
                trieNode.GetMemorySize(false) + ExpectedPerNodeKeyMemorySize);
        }


        [Test]
        public void Pruning_off_cache_should_not_change_commit_node()
        {
            TrieNode trieNode = new(NodeType.Leaf, Keccak.Zero);
            TrieNode trieNode2 = new(NodeType.Branch, TestItem.KeccakA);
            TrieNode trieNode3 = new(NodeType.Branch, TestItem.KeccakB);

            using TrieStore fullTrieStore = CreateTrieStore();
            IScopedTrieStore trieStore = fullTrieStore.GetTrieStore(null);
            trieStore.CommitNode(1234, new NodeCommitInfo(trieNode, TreePath.Empty));
            trieStore.FinishBlockCommit(TrieType.State, 1234, trieNode);
            trieStore.CommitNode(124, new NodeCommitInfo(trieNode2, TreePath.Empty));
            trieStore.CommitNode(11234, new NodeCommitInfo(trieNode3, TreePath.Empty));
            fullTrieStore.MemoryUsedByDirtyCache.Should().Be(0);
        }

        [Test]
        public void When_commit_forward_write_flag_if_available()
        {
            TrieNode trieNode = new(NodeType.Leaf, Keccak.Zero);

            TestMemDb testMemDb = new TestMemDb();

            using TrieStore fullTrieStore = CreateTrieStore(kvStore: testMemDb);
            IScopedTrieStore trieStore = fullTrieStore.GetTrieStore(null);
            trieStore.CommitNode(1234, new NodeCommitInfo(trieNode, TreePath.Empty), WriteFlags.LowPriority);
            trieStore.FinishBlockCommit(TrieType.State, 1234, trieNode, WriteFlags.LowPriority);

            if (_scheme == INodeStorage.KeyScheme.HalfPath)
            {
                testMemDb.KeyWasWrittenWithFlags(NodeStorage.GetHalfPathNodeStoragePath(null, TreePath.Empty, trieNode.Keccak), WriteFlags.LowPriority);
            }
            else
            {
                testMemDb.KeyWasWrittenWithFlags(trieNode.Keccak.Bytes.ToArray(), WriteFlags.LowPriority);
            }
        }

        [Test]
        public void Should_always_announce_block_number_when_pruning_disabled_and_persisting()
        {
            TrieNode trieNode = new(NodeType.Leaf, Keccak.Zero) { LastSeen = 1 };

            long reorgBoundaryCount = 0L;
            using TrieStore fullTrieStore = CreateTrieStore(persistenceStrategy: Archive.Instance);
            IScopedTrieStore trieStore = fullTrieStore.GetTrieStore(null);
            fullTrieStore.ReorgBoundaryReached += (_, e) => reorgBoundaryCount += e.BlockNumber;
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
            using TrieStore fullTrieStore = CreateTrieStore();
            IScopedTrieStore trieStore = fullTrieStore.GetTrieStore(null);
            fullTrieStore.ReorgBoundaryReached += (_, e) => reorgBoundaryCount += e.BlockNumber;
            trieStore.FinishBlockCommit(TrieType.State, 1, trieNode);
            trieStore.FinishBlockCommit(TrieType.State, 2, trieNode);
            trieStore.FinishBlockCommit(TrieType.State, 3, trieNode);
            trieStore.FinishBlockCommit(TrieType.State, 4, trieNode);
            reorgBoundaryCount.Should().Be(0L);
        }

        [Test]
        public void Pruning_off_cache_should_not_find_cached_or_unknown()
        {
            using TrieStore trieStore = CreateTrieStore();
            TrieNode returnedNode = trieStore.FindCachedOrUnknown(null, TreePath.Empty, TestItem.KeccakA);
            TrieNode returnedNode2 = trieStore.FindCachedOrUnknown(null, TreePath.Empty, TestItem.KeccakB);
            TrieNode returnedNode3 = trieStore.FindCachedOrUnknown(null, TreePath.Empty, TestItem.KeccakC);
            Assert.That(returnedNode.NodeType, Is.EqualTo(NodeType.Unknown));
            Assert.That(returnedNode2.NodeType, Is.EqualTo(NodeType.Unknown));
            Assert.That(returnedNode3.NodeType, Is.EqualTo(NodeType.Unknown));
            trieStore.MemoryUsedByDirtyCache.Should().Be(0);
        }

        [Test]
        public void FindCachedOrUnknown_CorrectlyCalculatedMemoryUsedByDirtyCache()
        {
            using TrieStore trieStore = CreateTrieStore(pruningStrategy: new TestPruningStrategy(true));
            long startSize = trieStore.MemoryUsedByDirtyCache;
            trieStore.FindCachedOrUnknown(null, TreePath.Empty, TestItem.KeccakA);
            TrieNode trieNode = new(NodeType.Leaf, Keccak.Zero);
            long oneKeccakSize = trieNode.GetMemorySize(false) + ExpectedPerNodeKeyMemorySize;
            Assert.That(trieStore.MemoryUsedByDirtyCache, Is.EqualTo(startSize + oneKeccakSize));
            trieStore.FindCachedOrUnknown(null, TreePath.Empty, TestItem.KeccakB);
            Assert.That(trieStore.MemoryUsedByDirtyCache, Is.EqualTo(2 * oneKeccakSize + startSize));
            trieStore.FindCachedOrUnknown(null, TreePath.Empty, TestItem.KeccakB);
            Assert.That(trieStore.MemoryUsedByDirtyCache, Is.EqualTo(2 * oneKeccakSize + startSize));
            trieStore.FindCachedOrUnknown(null, TreePath.Empty, TestItem.KeccakC);
            Assert.That(trieStore.MemoryUsedByDirtyCache, Is.EqualTo(3 * oneKeccakSize + startSize));
            trieStore.FindCachedOrUnknown(null, TreePath.Empty, TestItem.KeccakD, true);
            Assert.That(trieStore.MemoryUsedByDirtyCache, Is.EqualTo(3 * oneKeccakSize + startSize));
        }

        [Test]
        public void Memory_with_two_nodes_is_correct()
        {
            TrieNode trieNode1 = new(NodeType.Leaf, TestItem.KeccakA);
            TrieNode trieNode2 = new(NodeType.Leaf, TestItem.KeccakB);

            using TrieStore fullTrieStore = CreateTrieStore(pruningStrategy: new TestPruningStrategy(true));
            IScopedTrieStore trieStore = fullTrieStore.GetTrieStore(null);
            trieStore.CommitNode(1234, new NodeCommitInfo(trieNode1, TreePath.Empty));
            trieStore.CommitNode(1234, new NodeCommitInfo(trieNode2, TreePath.Empty));
            fullTrieStore.MemoryUsedByDirtyCache.Should().Be(
                trieNode1.GetMemorySize(false) + ExpectedPerNodeKeyMemorySize +
                trieNode2.GetMemorySize(false) + ExpectedPerNodeKeyMemorySize);
        }

        [Test]
        public void Memory_with_two_times_two_nodes_is_correct()
        {
            TrieNode trieNode1 = new(NodeType.Leaf, TestItem.KeccakA);
            TrieNode trieNode2 = new(NodeType.Leaf, TestItem.KeccakB);
            TrieNode trieNode3 = new(NodeType.Leaf, TestItem.KeccakA);
            TrieNode trieNode4 = new(NodeType.Leaf, TestItem.KeccakB);

            using TrieStore fullTrieStore = CreateTrieStore(pruningStrategy: new TestPruningStrategy(true));
            IScopedTrieStore trieStore = fullTrieStore.GetTrieStore(null);
            trieStore.CommitNode(1234, new NodeCommitInfo(trieNode1, TreePath.Empty));
            trieStore.CommitNode(1234, new NodeCommitInfo(trieNode2, TreePath.Empty));
            trieStore.FinishBlockCommit(TrieType.State, 1234, trieNode2);
            trieStore.CommitNode(1235, new NodeCommitInfo(trieNode3, TreePath.Empty));
            trieStore.CommitNode(1235, new NodeCommitInfo(trieNode4, TreePath.Empty));

            // depending on whether the node gets resolved it gives different values here in debugging and run
            // needs some attention
            fullTrieStore.MemoryUsedByDirtyCache.Should().BeLessOrEqualTo(
                trieNode1.GetMemorySize(false) + ExpectedPerNodeKeyMemorySize +
                trieNode2.GetMemorySize(false) + ExpectedPerNodeKeyMemorySize);
        }

        [Test]
        public void Dispatcher_will_try_to_clear_memory()
        {
            TrieNode trieNode1 = new(NodeType.Leaf, new byte[0]);
            TreePath emptyPath = TreePath.Empty;
            trieNode1.ResolveKey(null!, ref emptyPath, true);
            TrieNode trieNode2 = new(NodeType.Leaf, new byte[1]);
            trieNode2.ResolveKey(null!, ref emptyPath, true);

            TrieNode trieNode3 = new(NodeType.Leaf, new byte[2]);
            trieNode3.ResolveKey(null!, ref emptyPath, true);

            TrieNode trieNode4 = new(NodeType.Leaf, new byte[3]);
            trieNode4.ResolveKey(null!, ref emptyPath, true);

            using TrieStore fullTrieStore = CreateTrieStore(pruningStrategy: new MemoryLimit(640));
            IScopedTrieStore trieStore = fullTrieStore.GetTrieStore(null);
            trieStore.CommitNode(1234, new NodeCommitInfo(trieNode1, TreePath.Empty));
            trieStore.CommitNode(1234, new NodeCommitInfo(trieNode2, TreePath.Empty));
            trieStore.FinishBlockCommit(TrieType.State, 1234, trieNode2);
            trieStore.CommitNode(1235, new NodeCommitInfo(trieNode3, TreePath.Empty));
            trieStore.CommitNode(1235, new NodeCommitInfo(trieNode4, TreePath.Empty));
            trieStore.FinishBlockCommit(TrieType.State, 1235, trieNode2);
            trieStore.FinishBlockCommit(TrieType.State, 1236, trieNode2);
            fullTrieStore.MemoryUsedByDirtyCache.Should().Be(
                trieNode1.GetMemorySize(false) + ExpectedPerNodeKeyMemorySize +
                trieNode2.GetMemorySize(false) + ExpectedPerNodeKeyMemorySize +
                trieNode3.GetMemorySize(false) + ExpectedPerNodeKeyMemorySize +
                trieNode4.GetMemorySize(false) + ExpectedPerNodeKeyMemorySize);
        }

        [Test]
        public void Dispatcher_will_try_to_clear_memory_the_soonest_possible()
        {
            TrieNode trieNode1 = new(NodeType.Leaf, new byte[0]);
            TreePath emptyPath = TreePath.Empty;
            trieNode1.ResolveKey(null!, ref emptyPath, true);
            TrieNode trieNode2 = new(NodeType.Leaf, new byte[1]);
            trieNode2.ResolveKey(null!, ref emptyPath, true);

            TrieNode trieNode3 = new(NodeType.Leaf, new byte[2]);
            trieNode3.ResolveKey(null!, ref emptyPath, true);

            TrieNode trieNode4 = new(NodeType.Leaf, new byte[3]);
            trieNode4.ResolveKey(null!, ref emptyPath, true);

            using TrieStore fullTrieStore = CreateTrieStore(pruningStrategy: new MemoryLimit(512));
            IScopedTrieStore trieStore = fullTrieStore.GetTrieStore(null);
            trieStore.CommitNode(1234, new NodeCommitInfo(trieNode1, TreePath.Empty));
            trieStore.CommitNode(1234, new NodeCommitInfo(trieNode2, TreePath.Empty));
            trieStore.FinishBlockCommit(TrieType.State, 1234, trieNode2);
            trieStore.CommitNode(1235, new NodeCommitInfo(trieNode3, TreePath.Empty));
            trieStore.CommitNode(1235, new NodeCommitInfo(trieNode4, TreePath.Empty));
            fullTrieStore.MemoryUsedByDirtyCache.Should().Be(
                trieNode1.GetMemorySize(false) + ExpectedPerNodeKeyMemorySize +
                trieNode2.GetMemorySize(false) + ExpectedPerNodeKeyMemorySize +
                trieNode3.GetMemorySize(false) + ExpectedPerNodeKeyMemorySize +
                trieNode4.GetMemorySize(false) + ExpectedPerNodeKeyMemorySize);
        }

        [Test]
        public void Dispatcher_will_always_try_to_clear_memory()
        {
            TrieStore fullTrieStore = CreateTrieStore(pruningStrategy: new MemoryLimit(512));
            IScopedTrieStore trieStore = fullTrieStore.GetTrieStore(null);
            TreePath emptyPath = TreePath.Empty;
            for (int i = 0; i < 1024; i++)
            {
                for (int j = 0; j < 1 + i % 3; j++)
                {
                    TrieNode trieNode = new(NodeType.Leaf, new byte[0]); // 192B
                    trieNode.ResolveKey(NullTrieNodeResolver.Instance, ref emptyPath, true);
                    trieStore.CommitNode(i, new NodeCommitInfo(trieNode, TreePath.Empty));
                }

                TrieNode fakeRoot = new(NodeType.Leaf, new byte[0]); // 192B
                fakeRoot.ResolveKey(NullTrieNodeResolver.Instance, ref emptyPath, true);
                trieStore.FinishBlockCommit(TrieType.State, i, fakeRoot);
            }

            fullTrieStore.MemoryUsedByDirtyCache.Should().BeLessThan(512 * 2);
        }

        [Test]
        public void Dispatcher_will_save_to_db_everything_from_snapshot_blocks()
        {
            TrieNode a = new(NodeType.Leaf, new byte[0]); // 192B
            TreePath emptyPath = TreePath.Empty;
            a.ResolveKey(NullTrieNodeResolver.Instance, ref emptyPath, true);

            MemDb memDb = new();
            NodeStorage storage = new NodeStorage(memDb);

            using TrieStore fullTrieStore = CreateTrieStore(
                pruningStrategy: new MemoryLimit(16.MB()),
                kvStore: memDb,
                persistenceStrategy: new ConstantInterval(4));
            IScopedTrieStore trieStore = fullTrieStore.GetTrieStore(null);

            trieStore.CommitNode(0, new NodeCommitInfo(a, TreePath.Empty));
            trieStore.FinishBlockCommit(TrieType.State, 0, a);
            trieStore.FinishBlockCommit(TrieType.State, 1, a);
            trieStore.FinishBlockCommit(TrieType.State, 2, a);
            trieStore.FinishBlockCommit(TrieType.State, 3, a);
            trieStore.FinishBlockCommit(TrieType.State, 4, a);

            storage.Get(null, TreePath.Empty, a.Keccak).Should().NotBeNull();
            fullTrieStore.IsNodeCached(null, TreePath.Empty, a.Keccak).Should().BeTrue();
        }

        [Test]
        public void Stays_in_memory_until_persisted()
        {
            TrieNode a = new(NodeType.Leaf, new byte[0]); // 192B
            TreePath emptyPath = TreePath.Empty;
            a.ResolveKey(NullTrieNodeResolver.Instance, ref emptyPath, true);

            MemDb memDb = new();
            NodeStorage storage = new NodeStorage(memDb);

            using TrieStore fullTrieStore = CreateTrieStore(pruningStrategy: new MemoryLimit(16.MB()));
            IScopedTrieStore trieStore = fullTrieStore.GetTrieStore(null);

            trieStore.CommitNode(0, new NodeCommitInfo(a, TreePath.Empty));
            trieStore.FinishBlockCommit(TrieType.State, 0, a);
            trieStore.FinishBlockCommit(TrieType.State, 1, a);
            trieStore.FinishBlockCommit(TrieType.State, 2, a);
            trieStore.FinishBlockCommit(TrieType.State, 3, a);
            //  <- do not persist in this test

            storage.Get(null, TreePath.Empty, a.Keccak).Should().BeNull();
            fullTrieStore.IsNodeCached(null, TreePath.Empty, a.Keccak).Should().BeTrue();
        }

        [Test]
        public void Can_load_from_rlp()
        {
            MemDb memDb = new MemDb();
            NodeStorage storage = new NodeStorage(memDb);
            storage.Set(null, TreePath.Empty, Keccak.Zero, new byte[] { 1, 2, 3 }, WriteFlags.None);

            using TrieStore trieStore = CreateTrieStore(kvStore: memDb);
            trieStore.LoadRlp(null, TreePath.Empty, Keccak.Zero).Should().NotBeNull();
        }

        [Test]
        public void Will_get_persisted_on_snapshot_if_referenced()
        {
            TrieNode a = new(NodeType.Leaf, new byte[0]); // 192B
            TreePath emptyPath = TreePath.Empty;
            a.ResolveKey(NullTrieNodeResolver.Instance, ref emptyPath, true);

            MemDb memDb = new();
            NodeStorage storage = new NodeStorage(memDb);

            using TrieStore fullTrieStore = CreateTrieStore(
                kvStore: memDb,
                pruningStrategy: new MemoryLimit(16.MB()),
                persistenceStrategy: new ConstantInterval(4)
            );
            IScopedTrieStore trieStore = fullTrieStore.GetTrieStore(null);

            trieStore.FinishBlockCommit(TrieType.State, 0, null);
            trieStore.CommitNode(1, new NodeCommitInfo(a, TreePath.Empty));
            trieStore.FinishBlockCommit(TrieType.State, 1, a);
            trieStore.FinishBlockCommit(TrieType.State, 2, a);
            trieStore.FinishBlockCommit(TrieType.State, 3, a);
            trieStore.FinishBlockCommit(TrieType.State, 4, a);
            trieStore.FinishBlockCommit(TrieType.State, 5, a);
            trieStore.FinishBlockCommit(TrieType.State, 6, a);
            trieStore.FinishBlockCommit(TrieType.State, 7, a);
            trieStore.FinishBlockCommit(TrieType.State, 8, a);

            storage.Get(null, TreePath.Empty, a.Keccak).Should().NotBeNull();
            fullTrieStore.IsNodeCached(null, TreePath.Empty, a.Keccak).Should().BeTrue();
        }

        [Test]
        public void Will_not_get_dropped_on_snapshot_if_unreferenced_in_later_blocks()
        {
            TrieNode a = new(NodeType.Leaf, new byte[0]);
            TreePath emptyPath = TreePath.Empty;
            a.ResolveKey(NullTrieNodeResolver.Instance, ref emptyPath, true);

            TrieNode b = new(NodeType.Leaf, new byte[1]);
            b.ResolveKey(NullTrieNodeResolver.Instance, ref emptyPath, true);

            MemDb memDb = new();
            NodeStorage nodeStorage = new NodeStorage(memDb);

            using TrieStore fullTrieStore = CreateTrieStore(
                kvStore: memDb,
                pruningStrategy: new MemoryLimit(16.MB()),
                persistenceStrategy: new ConstantInterval(4));

            IScopedTrieStore trieStore = fullTrieStore.GetTrieStore(null);

            trieStore.FinishBlockCommit(TrieType.State, 0, null);
            trieStore.CommitNode(1, new NodeCommitInfo(a, TreePath.Empty));
            trieStore.FinishBlockCommit(TrieType.State, 1, a);
            trieStore.FinishBlockCommit(TrieType.State, 2, a);
            trieStore.FinishBlockCommit(TrieType.State, 3, a);
            trieStore.FinishBlockCommit(TrieType.State, 4, a);
            trieStore.FinishBlockCommit(TrieType.State, 5, a);
            trieStore.FinishBlockCommit(TrieType.State, 6, a);
            trieStore.CommitNode(7, new NodeCommitInfo(b, TreePath.Empty));
            trieStore.FinishBlockCommit(TrieType.State, 7, b);
            trieStore.FinishBlockCommit(TrieType.State, 8, b);

            nodeStorage.Get(null, TreePath.Empty, a.Keccak).Should().NotBeNull();
            fullTrieStore.IsNodeCached(null, TreePath.Empty, a.Keccak).Should().BeTrue();
        }

        [Test]
        public void Will_get_dropped_on_snapshot_if_it_was_a_transient_node()
        {
            TrieNode a = new(NodeType.Leaf, new byte[] { 1 });
            TreePath emptyPath = TreePath.Empty;
            a.ResolveKey(NullTrieNodeResolver.Instance, ref emptyPath, true);

            TrieNode b = new(NodeType.Leaf, new byte[] { 2 });
            b.ResolveKey(NullTrieNodeResolver.Instance, ref emptyPath, true);

            MemDb memDb = new();

            using TrieStore fullTrieStore = CreateTrieStore(
                kvStore: memDb,
                pruningStrategy: new MemoryLimit(16.MB()),
                persistenceStrategy: new ConstantInterval(4));

            IScopedTrieStore trieStore = fullTrieStore.GetTrieStore(null);

            trieStore.FinishBlockCommit(TrieType.State, 0, null);
            trieStore.CommitNode(1, new NodeCommitInfo(a, TreePath.Empty));
            trieStore.FinishBlockCommit(TrieType.State, 1, a);
            trieStore.FinishBlockCommit(TrieType.State, 2, a);
            trieStore.CommitNode(3, new NodeCommitInfo(b, TreePath.Empty)); // <- new root
            trieStore.FinishBlockCommit(TrieType.State, 3, b);
            trieStore.FinishBlockCommit(TrieType.State, 4, b); // should be 'a' to test properly
            trieStore.FinishBlockCommit(TrieType.State, 5, b); // should be 'a' to test properly
            trieStore.FinishBlockCommit(TrieType.State, 6, b); // should be 'a' to test properly
            trieStore.FinishBlockCommit(TrieType.State, 7, b); // should be 'a' to test properly
            trieStore.FinishBlockCommit(TrieType.State, 8, b); // should be 'a' to test properly

            memDb[a.Keccak!.Bytes].Should().BeNull();
            fullTrieStore.IsNodeCached(null, TreePath.Empty, a.Keccak).Should().BeTrue();
        }

        private class BadDb : IKeyValueStoreWithBatching
        {
            private readonly Dictionary<byte[], byte[]> _db = new();

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

            public IWriteBatch StartWriteBatch()
            {
                return new BadWriteBatch();
            }

            private class BadWriteBatch : IWriteBatch
            {
                private readonly Dictionary<byte[], byte[]> _inBatched = new();

                public void Dispose()
                {
                }

                public byte[]? this[ReadOnlySpan<byte> key]
                {
                    set => Set(key, value);
                }

                public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
                {
                    _inBatched[key.ToArray()] = value;
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

        [Test]
        public void Will_store_storage_on_snapshot()
        {
            TrieNode storage1 = new(NodeType.Leaf, new byte[2]);
            TreePath emptyPath = TreePath.Empty;
            storage1.ResolveKey(NullTrieNodeResolver.Instance, ref emptyPath, true);

            TrieNode a = new(NodeType.Leaf);
            Account account = new(1, 1, storage1.Keccak, Keccak.OfAnEmptyString);
            a.Value = _accountDecoder.Encode(account).Bytes;
            a.Key = Nibbles.BytesToNibbleBytes(TestItem.KeccakA.BytesToArray());
            a.ResolveKey(NullTrieNodeResolver.Instance, ref emptyPath, true);

            MemDb memDb = new();
            NodeStorage asStorage = new NodeStorage(memDb);

            using TrieStore fullTrieStore = CreateTrieStore(
                kvStore: memDb,
                pruningStrategy: new MemoryLimit(16.MB()),
                persistenceStrategy: new ConstantInterval(4));

            IScopedTrieStore trieStore = fullTrieStore.GetTrieStore(null);
            trieStore.FinishBlockCommit(TrieType.State, 0, null);
            trieStore.CommitNode(1, new NodeCommitInfo(a, TreePath.Empty));
            fullTrieStore.GetTrieStore(TestItem.KeccakA)
                .CommitNode(1, new NodeCommitInfo(storage1, TreePath.Empty));
            fullTrieStore.GetTrieStore(TestItem.KeccakA)
                .FinishBlockCommit(TrieType.Storage, 1, storage1);
            trieStore.FinishBlockCommit(TrieType.Storage, 1, storage1);
            trieStore.FinishBlockCommit(TrieType.State, 1, a);
            trieStore.FinishBlockCommit(TrieType.State, 2, a);
            trieStore.FinishBlockCommit(TrieType.State, 3, a);
            trieStore.FinishBlockCommit(TrieType.State, 4, a);
            trieStore.FinishBlockCommit(TrieType.State, 5, a);
            trieStore.FinishBlockCommit(TrieType.State, 6, a);
            trieStore.FinishBlockCommit(TrieType.State, 7, a);
            trieStore.FinishBlockCommit(TrieType.State, 8, a);

            asStorage.Get(null, TreePath.Empty, a.Keccak).Should().NotBeNull();
            asStorage.Get(TestItem.KeccakA, TreePath.Empty, storage1.Keccak).Should().NotBeNull();
            fullTrieStore.IsNodeCached(null, TreePath.Empty, a.Keccak).Should().BeTrue();
            // trieStore.IsInMemory(storage1.Keccak).Should().BeFalse();
        }

        [Test]
        public void Will_drop_transient_storage()
        {
            TrieNode storage1 = new(NodeType.Leaf, new byte[2]);
            TreePath emptyPath = TreePath.Empty;
            storage1.ResolveKey(NullTrieNodeResolver.Instance, ref emptyPath, true);

            TrieNode a = new(NodeType.Leaf);
            Account account = new(1, 1, storage1.Keccak, Keccak.OfAnEmptyString);
            a.Value = _accountDecoder.Encode(account).Bytes;
            a.Key = Bytes.FromHexString("abc");
            a.ResolveKey(NullTrieNodeResolver.Instance, ref emptyPath, true);

            TrieNode b = new(NodeType.Leaf, new byte[1]);
            b.ResolveKey(NullTrieNodeResolver.Instance, ref emptyPath, true);

            MemDb memDb = new();

            using TrieStore fullTrieStore = CreateTrieStore(
                kvStore: memDb,
                pruningStrategy: new MemoryLimit(16.MB()),
                persistenceStrategy: new ConstantInterval(4));

            IScopedTrieStore trieStore = fullTrieStore.GetTrieStore(null);

            trieStore.FinishBlockCommit(TrieType.State, 0, null);
            trieStore.CommitNode(1, new NodeCommitInfo(a, TreePath.Empty));
            trieStore.CommitNode(1, new NodeCommitInfo(storage1, TreePath.Empty));
            trieStore.FinishBlockCommit(TrieType.Storage, 1, storage1);
            trieStore.FinishBlockCommit(TrieType.State, 1, a);
            trieStore.FinishBlockCommit(TrieType.State, 2, a);
            trieStore.CommitNode(3, new NodeCommitInfo(b, TreePath.Empty)); // <- new root
            trieStore.FinishBlockCommit(TrieType.State, 3, b);
            trieStore.FinishBlockCommit(TrieType.State, 4, b); // should be 'a' to test properly
            trieStore.FinishBlockCommit(TrieType.State, 5, b); // should be 'a' to test properly
            trieStore.FinishBlockCommit(TrieType.State, 6, b); // should be 'a' to test properly
            trieStore.FinishBlockCommit(TrieType.State, 7, b); // should be 'a' to test properly
            trieStore.FinishBlockCommit(TrieType.State, 8, b); // should be 'a' to test properly

            memDb[a.Keccak!.Bytes].Should().BeNull();
            memDb[storage1.Keccak!.Bytes].Should().BeNull();
            fullTrieStore.IsNodeCached(null, TreePath.Empty, a.Keccak).Should().BeTrue();
            fullTrieStore.IsNodeCached(null, TreePath.Empty, storage1.Keccak).Should().BeTrue();
        }

        [Test]
        public void Will_combine_same_storage()
        {
            byte[] storage1Nib = Nibbles.BytesToNibbleBytes(TestItem.KeccakA.BytesToArray());
            storage1Nib[0] = 0;
            byte[] storage2Nib = Nibbles.BytesToNibbleBytes(TestItem.KeccakA.BytesToArray());
            storage2Nib[0] = 1;

            TrieNode storage1 = new(NodeType.Leaf, new byte[32]);
            TreePath emptyPath = TreePath.Empty;
            storage1.ResolveKey(NullTrieNodeResolver.Instance, ref emptyPath, true);

            TrieNode a = new(NodeType.Leaf);
            Account account = new(1, 1, storage1.Keccak, Keccak.OfAnEmptyString);
            a.Value = _accountDecoder.Encode(account).Bytes;
            a.Key = storage1Nib[1..];
            a.ResolveKey(NullTrieNodeResolver.Instance, ref emptyPath, true);

            TrieNode storage2 = new(NodeType.Leaf, new byte[32]);
            storage2.ResolveKey(NullTrieNodeResolver.Instance, ref emptyPath, true);

            TrieNode b = new(NodeType.Leaf);
            Account accountB = new(2, 1, storage2.Keccak, Keccak.OfAnEmptyString);
            b.Value = _accountDecoder.Encode(accountB).Bytes;
            b.Key = storage2Nib[1..];
            b.ResolveKey(NullTrieNodeResolver.Instance, ref emptyPath, true);

            TrieNode branch = new(NodeType.Branch);
            branch.SetChild(0, a);
            branch.SetChild(1, b);
            branch.ResolveKey(NullTrieStore.Instance, ref emptyPath, true);

            MemDb memDb = new();
            NodeStorage storage = new NodeStorage(memDb);

            using TrieStore fullTrieStore = CreateTrieStore(
                kvStore: memDb,
                pruningStrategy: new MemoryLimit(16.MB()),
                persistenceStrategy: new ConstantInterval(4));

            IScopedTrieStore trieStore = fullTrieStore.GetTrieStore(null);

            trieStore.FinishBlockCommit(TrieType.State, 0, null);

            IScopedTrieStore storageTrieStore = fullTrieStore.GetTrieStore(new Hash256(Nibbles.ToBytes(storage1Nib)));
            storageTrieStore.CommitNode(1, new NodeCommitInfo(storage1, TreePath.Empty));
            storageTrieStore.FinishBlockCommit(TrieType.Storage, 1, storage1);

            storageTrieStore = fullTrieStore.GetTrieStore(new Hash256(Nibbles.ToBytes(storage2Nib)));
            storageTrieStore.CommitNode(1, new NodeCommitInfo(storage2, TreePath.Empty));
            storageTrieStore.FinishBlockCommit(TrieType.Storage, 1, storage2);

            trieStore.CommitNode(1, new NodeCommitInfo(a, TreePath.Empty));
            trieStore.CommitNode(1, new NodeCommitInfo(b, TreePath.Empty));
            trieStore.CommitNode(1, new NodeCommitInfo(branch, TreePath.Empty));
            trieStore.FinishBlockCommit(TrieType.State, 1, branch);
            trieStore.FinishBlockCommit(TrieType.State, 2, branch);
            trieStore.FinishBlockCommit(TrieType.State, 3, branch);
            trieStore.FinishBlockCommit(TrieType.State, 4, branch);
            trieStore.FinishBlockCommit(TrieType.State, 5, branch);
            trieStore.FinishBlockCommit(TrieType.State, 6, branch);
            trieStore.FinishBlockCommit(TrieType.State, 7, branch);
            trieStore.FinishBlockCommit(TrieType.State, 8, branch);

            storage.Get(null, TreePath.FromNibble(new byte[] { 0 }), a.Keccak).Should().NotBeNull();
            storage.Get(new Hash256(Nibbles.ToBytes(storage1Nib)), TreePath.Empty, storage1.Keccak).Should().NotBeNull();
            fullTrieStore.IsNodeCached(null, TreePath.Empty, a.Keccak).Should().BeTrue();
            fullTrieStore.IsNodeCached(new Hash256(Nibbles.ToBytes(storage1Nib)), TreePath.Empty, storage1.Keccak).Should().BeTrue();
        }

        [Test]
        public void ReadOnly_store_doesnt_change_witness()
        {
            TrieNode node = new(NodeType.Leaf);
            Account account = new(1, 1, TestItem.KeccakA, Keccak.OfAnEmptyString);
            node.Value = _accountDecoder.Encode(account).Bytes;
            node.Key = Bytes.FromHexString("abc");
            TreePath emptyPath = TreePath.Empty;
            node.ResolveKey(NullTrieNodeResolver.Instance, ref emptyPath, true);

            MemDb originalStore = new MemDb();
            WitnessCollector witnessCollector = new WitnessCollector(new MemDb(), LimboLogs.Instance);
            IKeyValueStoreWithBatching store = originalStore.WitnessedBy(witnessCollector);
            using TrieStore fullTrieStore = CreateTrieStore(pruningStrategy: new TestPruningStrategy(false), kvStore: store);
            IScopedTrieStore trieStore = fullTrieStore.GetTrieStore(null);
            trieStore.CommitNode(0, new NodeCommitInfo(node, TreePath.Empty));
            trieStore.FinishBlockCommit(TrieType.State, 0, node);

            IReadOnlyTrieStore readOnlyTrieStore = fullTrieStore.AsReadOnly(new NodeStorage(originalStore));
            readOnlyTrieStore.LoadRlp(null, TreePath.Empty, node.Keccak);

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
            using TrieStore fullTrieStore = CreateTrieStore(
                kvStore: memDb,
                pruningStrategy: new MemoryLimit(10.MB()),
                persistenceStrategy: new ConstantInterval(10));

            IScopedTrieStore trieStore = fullTrieStore.GetTrieStore(null);
            TreePath emptyPath = TreePath.Empty;
            trieNode.ResolveKey(trieStore, ref emptyPath, false);
            trieStore.CommitNode(1, new NodeCommitInfo(trieNode, TreePath.Empty));

            if (beThreadSafe)
            {
                trieStore = fullTrieStore.AsReadOnly().GetTrieStore(null);
            }

            void CheckChildren()
            {
                for (int i = 0; i < 16 * 10; i++)
                {
                    try
                    {
                        trieStore.FindCachedOrUnknown(TreePath.Empty, trieNode.Keccak).GetChildHash(i % 16).Should().BeEquivalentTo(TestItem.Keccaks[i % 16], i.ToString());
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
            node.Key = Nibbles.BytesToNibbleBytes(TestItem.KeccakA.BytesToArray());
            TreePath emptyPath = TreePath.Empty;
            node.ResolveKey(NullTrieNodeResolver.Instance, ref emptyPath, true);

            using TrieStore fullTrieStore = CreateTrieStore(pruningStrategy: new TestPruningStrategy(pruning));
            IScopedTrieStore trieStore = fullTrieStore.GetTrieStore(null);
            trieStore.CommitNode(0, new NodeCommitInfo(node, TreePath.Empty));
            trieStore.FinishBlockCommit(TrieType.State, 0, node);
            var originalNode = trieStore.FindCachedOrUnknown(TreePath.Empty, node.Keccak);

            IReadOnlyTrieStore readOnlyTrieStore = fullTrieStore.AsReadOnly();
            var readOnlyNode = readOnlyTrieStore.FindCachedOrUnknown(null, TreePath.Empty, node.Keccak);

            readOnlyNode.Should().NotBe(originalNode);
            readOnlyNode.Should().BeEquivalentTo(originalNode,
                eq => eq.Including(t => t.Keccak)
                    .Including(t => t.NodeType));

            var origRlp = originalNode.FullRlp;
            var readOnlyRlp = readOnlyNode.FullRlp;
            readOnlyRlp.Should().BeEquivalentTo(origRlp);

            readOnlyNode.Key?.ToString().Should().Be(originalNode.Key?.ToString());
        }

        private long ExpectedPerNodeKeyMemorySize => _scheme == INodeStorage.KeyScheme.Hash ? 0 : TrieStore.DirtyNodesCache.Key.MemoryUsage;

        [Test]
        public void After_commit_should_have_has_root()
        {
            MemDb db = new();
            TrieStore trieStore = new TrieStore(db, LimboLogs.Instance);
            trieStore.HasRoot(Keccak.EmptyTreeHash).Should().BeTrue();

            Account account = new(1);
            StateTree stateTree = new(trieStore, LimboLogs.Instance);
            stateTree.Set(TestItem.AddressA, account);
            stateTree.Commit(0);
            trieStore.HasRoot(stateTree.RootHash).Should().BeTrue();

            stateTree.Get(TestItem.AddressA);
            account = account.WithChangedBalance(2);
            stateTree.Set(TestItem.AddressA, account);
            stateTree.Commit(0);
            trieStore.HasRoot(stateTree.RootHash).Should().BeTrue();
        }

        public async Task Will_RemovePastKeys_OnSnapshot()
        {
            MemDb memDb = new();

            using TrieStore fullTrieStore = CreateTrieStore(
                kvStore: memDb,
                pruningStrategy: new TestPruningStrategy(true, true, 100000),
                persistenceStrategy: No.Persistence,
                reorgDepthOverride: 2);

            IScopedTrieStore trieStore = fullTrieStore.GetTrieStore(null);

            for (int i = 0; i < 64; i++)
            {
                TrieNode node = new(NodeType.Leaf, TestItem.Keccaks[i], new byte[2]);
                trieStore.CommitNode(i, new NodeCommitInfo(node, TreePath.Empty));
                trieStore.FinishBlockCommit(TrieType.State, i, node);

                // Pruning is done in background
                await Task.Delay(TimeSpan.FromMilliseconds(10));
            }

            if (_scheme == INodeStorage.KeyScheme.Hash)
            {
                memDb.Count.Should().NotBe(1);
            }
            else
            {
                memDb.Count.Should().Be(1);
            }
        }

        [Test]
        public async Task Will_NotRemove_ReCommittedNode()
        {
            MemDb memDb = new();

            using TrieStore fullTrieStore = CreateTrieStore(
                kvStore: memDb,
                pruningStrategy: new TestPruningStrategy(true, true, 100000),
                persistenceStrategy: No.Persistence,
                reorgDepthOverride: 2);

            IScopedTrieStore trieStore = fullTrieStore.GetTrieStore(null);

            for (int i = 0; i < 64; i++)
            {
                TrieNode node = new(NodeType.Leaf, TestItem.Keccaks[i % 4], new byte[2]);
                trieStore.CommitNode(i, new NodeCommitInfo(node, TreePath.Empty));
                node = trieStore.FindCachedOrUnknown(TreePath.Empty, node.Keccak);
                trieStore.FinishBlockCommit(TrieType.State, i, node);

                // Pruning is done in background
                await Task.Delay(TimeSpan.FromMilliseconds(10));
            }

            memDb.Count.Should().Be(4);
        }
    }
}
