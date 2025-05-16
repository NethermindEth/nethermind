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
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using NSubstitute;
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
            IPruningConfig? pruningConfig = null
        )
        {
            pruningStrategy ??= No.Pruning;
            kvStore ??= new TestMemDb();
            persistenceStrategy ??= No.Persistence;
            return new(
                new NodeStorage(kvStore, _scheme, requirePath: _scheme == INodeStorage.KeyScheme.HalfPath),
                pruningStrategy,
                persistenceStrategy,
                pruningConfig ?? new PruningConfig()
                {
                    TrackPastKeys = false // Default disable
                },
                _logManager);
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
            TreePath emptyPath = TreePath.Empty;
            using (ICommitter? committer = fullTrieStore.BeginStateBlockCommit(1234, null))
            {
                committer.CommitNode(ref emptyPath, new NodeCommitInfo(trieNode));
            }
            fullTrieStore.MemoryUsedByDirtyCache.Should().Be(
                trieNode.GetMemorySize(false) + ExpectedPerNodeKeyMemorySize);
        }

        [Test]
        public void Flush_ShouldBeCalledOnEachPersist()
        {
            TrieNode trieNode = new(NodeType.Leaf, Keccak.Zero);

            TestMemDb testMemDb = new TestMemDb();
            using TrieStore fullTrieStore = CreateTrieStore(persistenceStrategy: Archive.Instance, kvStore: testMemDb);
            PatriciaTree pt = new PatriciaTree(fullTrieStore.GetTrieStore(null), LimboLogs.Instance);

            for (int i = 0; i < 4; i++)
            {
                pt.Set(TestItem.KeccakA.BytesToArray(), TestItem.Keccaks[i].BytesToArray());
                using (ICommitter? committer = fullTrieStore.BeginStateBlockCommit(i + 1, trieNode))
                {
                    pt.Commit();
                }
            }

            testMemDb.FlushCount.Should().Be(4);
        }

        [Test]
        public void Pruning_off_cache_should_not_change_commit_node()
        {
            TrieNode trieNode = new(NodeType.Leaf, Keccak.Zero);
            TrieNode trieNode2 = new(NodeType.Branch, TestItem.KeccakA);
            TrieNode trieNode3 = new(NodeType.Branch, TestItem.KeccakB);

            using TrieStore fullTrieStore = CreateTrieStore();
            TreePath emptyPath = TreePath.Empty;
            using (ICommitter? committer = fullTrieStore.BeginStateBlockCommit(1234, trieNode))
            {
                committer.CommitNode(ref emptyPath, new NodeCommitInfo(trieNode));
            }
            using (ICommitter? committer = fullTrieStore.BeginStateBlockCommit(1235, trieNode))
            {
                committer.CommitNode(ref emptyPath, new NodeCommitInfo(trieNode2));
                committer.CommitNode(ref emptyPath, new NodeCommitInfo(trieNode3));
            }
            fullTrieStore.MemoryUsedByDirtyCache.Should().Be(0);
        }

        [Test]
        public void When_commit_forward_write_flag_if_available()
        {
            TrieNode trieNode = new(NodeType.Leaf, Keccak.Zero);

            TestMemDb testMemDb = new TestMemDb();

            using TrieStore fullTrieStore = CreateTrieStore(kvStore: testMemDb);

            TreePath emptyPath = TreePath.Empty;
            using (ICommitter? committer = fullTrieStore.BeginStateBlockCommit(1234, trieNode, WriteFlags.LowPriority))
            {
                committer.CommitNode(ref emptyPath, new NodeCommitInfo(trieNode));
            }

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
            fullTrieStore.ReorgBoundaryReached += (_, e) => reorgBoundaryCount += e.BlockNumber;
            fullTrieStore.BeginStateBlockCommit(1, trieNode).Dispose();
            reorgBoundaryCount.Should().Be(0);
            fullTrieStore.BeginStateBlockCommit(2, trieNode).Dispose();
            reorgBoundaryCount.Should().Be(1);
            fullTrieStore.BeginStateBlockCommit(3, trieNode).Dispose();
            reorgBoundaryCount.Should().Be(3);
            fullTrieStore.BeginStateBlockCommit(4, trieNode).Dispose();
            reorgBoundaryCount.Should().Be(6);
        }

        [Test]
        public void Should_always_announce_zero_when_not_persisting()
        {
            TrieNode trieNode = new(NodeType.Leaf, Keccak.Zero);

            long reorgBoundaryCount = 0L;
            using TrieStore fullTrieStore = CreateTrieStore();
            fullTrieStore.ReorgBoundaryReached += (_, e) => reorgBoundaryCount += e.BlockNumber;
            fullTrieStore.BeginStateBlockCommit(1, trieNode).Dispose();
            fullTrieStore.BeginStateBlockCommit(2, trieNode).Dispose();
            fullTrieStore.BeginStateBlockCommit(3, trieNode).Dispose();
            fullTrieStore.BeginStateBlockCommit(4, trieNode).Dispose();
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
            long oneKeccakSize = trieNode.GetMemorySize(false) + ExpectedPerNodeKeyMemorySize - MemorySizes.SmallObjectOverhead;
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
            TreePath emptyPath = TreePath.Empty;
            using (ICommitter committer = fullTrieStore.BeginStateBlockCommit(1234, null))
            {
                committer.CommitNode(ref emptyPath, new NodeCommitInfo(trieNode1));
                committer.CommitNode(ref emptyPath, new NodeCommitInfo(trieNode2));
            }
            fullTrieStore.MemoryUsedByDirtyCache.Should().Be(
                trieNode1.GetMemorySize(false) + ExpectedPerNodeKeyMemorySize +
                trieNode2.GetMemorySize(false) + ExpectedPerNodeKeyMemorySize);
        }

        [Test]
        public void Memory_with_concurrent_commits_is_correct()
        {
            using TrieStore fullTrieStore = CreateTrieStore(pruningStrategy: new TestPruningStrategy(true));

            IScopedTrieStore trieStore = fullTrieStore.GetTrieStore(null);
            PatriciaTree tree = new PatriciaTree(trieStore, LimboLogs.Instance);

            Random rand = new Random(0);

            Span<byte> key = stackalloc byte[32];
            Span<byte> value = stackalloc byte[32];
            for (int i = 0; i < 1000; i++)
            {
                rand.NextBytes(key);
                rand.NextBytes(value);

                tree.Set(key, value.ToArray());
            }

            using (fullTrieStore.BeginBlockCommit(0))
            {
                tree.Commit();
            }

            fullTrieStore.MemoryUsedByDirtyCache.Should().Be(_scheme == INodeStorage.KeyScheme.Hash ? 589124 : 659272L);
            fullTrieStore.CommittedNodesCount.Should().Be(1349);
        }

        [Test]
        public void Memory_with_two_times_two_nodes_is_correct()
        {
            TrieNode trieNode1 = new(NodeType.Leaf, TestItem.KeccakA);
            TrieNode trieNode2 = new(NodeType.Leaf, TestItem.KeccakB);
            TrieNode trieNode3 = new(NodeType.Leaf, TestItem.KeccakA);
            TrieNode trieNode4 = new(NodeType.Leaf, TestItem.KeccakB);

            using TrieStore fullTrieStore = CreateTrieStore(pruningStrategy: new TestPruningStrategy(true));
            TreePath emptyPath = TreePath.Empty;
            using (ICommitter? committer = fullTrieStore.BeginStateBlockCommit(1234, trieNode2))
            {
                committer.CommitNode(ref emptyPath, new NodeCommitInfo(trieNode1));
                committer.CommitNode(ref emptyPath, new NodeCommitInfo(trieNode2));
            }

            using (ICommitter? committer = fullTrieStore.BeginStateBlockCommit(1235, trieNode2))
            {
                committer.CommitNode(ref emptyPath, new NodeCommitInfo(trieNode3));
                committer.CommitNode(ref emptyPath, new NodeCommitInfo(trieNode4));
            }

            // depending on whether the node gets resolved it gives different values here in debugging and run
            // needs some attention
            fullTrieStore.MemoryUsedByDirtyCache.Should().BeLessThanOrEqualTo(
                trieNode1.GetMemorySize(false) + ExpectedPerNodeKeyMemorySize +
                trieNode2.GetMemorySize(false) + ExpectedPerNodeKeyMemorySize);
        }

        [Test]
        public void Dispatcher_will_try_to_clear_memory()
        {
            TrieNode trieNode1 = new(NodeType.Leaf, []);
            TreePath emptyPath = TreePath.Empty;
            trieNode1.ResolveKey(null!, ref emptyPath, true);
            TrieNode trieNode2 = new(NodeType.Leaf, new byte[1]);
            trieNode2.ResolveKey(null!, ref emptyPath, true);

            TrieNode trieNode3 = new(NodeType.Leaf, new byte[2]);
            trieNode3.ResolveKey(null!, ref emptyPath, true);

            TrieNode trieNode4 = new(NodeType.Leaf, new byte[3]);
            trieNode4.ResolveKey(null!, ref emptyPath, true);

            using TrieStore fullTrieStore = CreateTrieStore(pruningStrategy: new MemoryLimit(640));

            using (ICommitter committer = fullTrieStore.BeginStateBlockCommit(1234, trieNode2))
            {
                committer.CommitNode(ref emptyPath, new NodeCommitInfo(trieNode1));
                committer.CommitNode(ref emptyPath, new NodeCommitInfo(trieNode2));
            }

            using (ICommitter committer = fullTrieStore.BeginStateBlockCommit(1235, trieNode2))
            {
                committer.CommitNode(ref emptyPath, new NodeCommitInfo(trieNode3));
                committer.CommitNode(ref emptyPath, new NodeCommitInfo(trieNode4));
            }

            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(1236, trieNode2)) { }

            fullTrieStore.MemoryUsedByDirtyCache.Should().Be(
                trieNode1.GetMemorySize(false) + ExpectedPerNodeKeyMemorySize +
                trieNode2.GetMemorySize(false) + ExpectedPerNodeKeyMemorySize +
                trieNode3.GetMemorySize(false) + ExpectedPerNodeKeyMemorySize +
                trieNode4.GetMemorySize(false) + ExpectedPerNodeKeyMemorySize);
        }

        [Test]
        public void Dispatcher_will_try_to_clear_memory_the_soonest_possible()
        {
            TrieNode trieNode1 = new(NodeType.Leaf, []);
            TreePath emptyPath = TreePath.Empty;
            trieNode1.ResolveKey(null!, ref emptyPath, true);
            TrieNode trieNode2 = new(NodeType.Leaf, new byte[1]);
            trieNode2.ResolveKey(null!, ref emptyPath, true);

            TrieNode trieNode3 = new(NodeType.Leaf, new byte[2]);
            trieNode3.ResolveKey(null!, ref emptyPath, true);

            TrieNode trieNode4 = new(NodeType.Leaf, new byte[3]);
            trieNode4.ResolveKey(null!, ref emptyPath, true);

            using TrieStore fullTrieStore = CreateTrieStore(pruningStrategy: new MemoryLimit(512));

            using (ICommitter committer = fullTrieStore.BeginStateBlockCommit(1234, trieNode2))
            {
                committer.CommitNode(ref emptyPath, new NodeCommitInfo(trieNode1));
                committer.CommitNode(ref emptyPath, new NodeCommitInfo(trieNode2));
            }

            using (ICommitter committer = fullTrieStore.BeginStateBlockCommit(1235, trieNode2))
            {
                committer.CommitNode(ref emptyPath, new NodeCommitInfo(trieNode3));
                committer.CommitNode(ref emptyPath, new NodeCommitInfo(trieNode4));
            }

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
            TreePath emptyPath = TreePath.Empty;
            for (int i = 0; i < 1024; i++)
            {
                TrieNode fakeRoot = new(NodeType.Leaf, []); // 192B
                fakeRoot.ResolveKey(NullTrieNodeResolver.Instance, ref emptyPath, true);
                using (ICommitter committer = fullTrieStore.BeginStateBlockCommit(i, fakeRoot))
                {
                    for (int j = 0; j < 1 + i % 3; j++)
                    {
                        TrieNode trieNode = new(NodeType.Leaf, []); // 192B
                        trieNode.ResolveKey(NullTrieNodeResolver.Instance, ref emptyPath, true);
                        committer.CommitNode(ref emptyPath, new NodeCommitInfo(trieNode));
                    }
                }
            }

            fullTrieStore.MemoryUsedByDirtyCache.Should().BeLessThan(512 * 2);
        }

        [Test]
        public void Dispatcher_will_save_to_db_everything_from_snapshot_blocks()
        {
            TrieNode a = new(NodeType.Leaf, []); // 192B
            TreePath emptyPath = TreePath.Empty;
            a.ResolveKey(NullTrieNodeResolver.Instance, ref emptyPath, true);

            MemDb memDb = new();
            NodeStorage storage = new NodeStorage(memDb);

            using TrieStore fullTrieStore = CreateTrieStore(
                pruningStrategy: new MemoryLimit(16.MB()),
                kvStore: memDb,
                persistenceStrategy: new ConstantInterval(4));

            using (ICommitter committer = fullTrieStore.BeginStateBlockCommit(0, a))
            {
                committer.CommitNode(ref emptyPath, new NodeCommitInfo(a));
            }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(1, a)) { }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(2, a)) { }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(3, a)) { }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(4, a)) { }

            storage.Get(null, TreePath.Empty, a.Keccak).Should().NotBeNull();
            fullTrieStore.IsNodeCached(null, TreePath.Empty, a.Keccak).Should().BeTrue();
        }

        [Test]
        public void Stays_in_memory_until_persisted()
        {
            TrieNode a = new(NodeType.Leaf, []); // 192B
            TreePath emptyPath = TreePath.Empty;
            a.ResolveKey(NullTrieNodeResolver.Instance, ref emptyPath, true);

            MemDb memDb = new();
            NodeStorage storage = new NodeStorage(memDb);

            using TrieStore fullTrieStore = CreateTrieStore(pruningStrategy: new MemoryLimit(16.MB()));

            using (ICommitter committer = fullTrieStore.BeginStateBlockCommit(0, a))
            {
                committer.CommitNode(ref emptyPath, new NodeCommitInfo(a));
            }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(1, a)) { }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(2, a)) { }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(3, a)) { }
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
            TrieNode a = new(NodeType.Leaf, []); // 192B
            TreePath emptyPath = TreePath.Empty;
            a.ResolveKey(NullTrieNodeResolver.Instance, ref emptyPath, true);

            MemDb memDb = new();
            NodeStorage storage = new NodeStorage(memDb);

            using TrieStore fullTrieStore = CreateTrieStore(
                kvStore: memDb,
                pruningStrategy: new MemoryLimit(16.MB()),
                persistenceStrategy: new ConstantInterval(4)
            );

            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(0, null)) { }
            using (ICommitter committer = fullTrieStore.BeginStateBlockCommit(1, a))
            {
                committer.CommitNode(ref emptyPath, new NodeCommitInfo(a));
            }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(2, a)) { }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(3, a)) { }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(4, a)) { }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(5, a)) { }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(6, a)) { }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(7, a)) { }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(8, a)) { }

            storage.Get(null, TreePath.Empty, a.Keccak).Should().NotBeNull();
            fullTrieStore.IsNodeCached(null, TreePath.Empty, a.Keccak).Should().BeTrue();
        }

        [Test]
        public void Will_not_get_dropped_on_snapshot_if_unreferenced_in_later_blocks()
        {
            TrieNode a = new(NodeType.Leaf, []);
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

            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(0, null)) { }
            using (ICommitter committer = fullTrieStore.BeginStateBlockCommit(1, a))
            {
                committer.CommitNode(ref emptyPath, new NodeCommitInfo(a));
            }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(2, a)) { }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(3, a)) { }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(4, a)) { }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(5, a)) { }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(6, a)) { }
            using (ICommitter committer = fullTrieStore.BeginStateBlockCommit(7, a))
            {
                committer.CommitNode(ref emptyPath, new NodeCommitInfo(b));
            }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(8, a)) { }

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

            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(0, null)) { }
            using (ICommitter committer = fullTrieStore.BeginStateBlockCommit(1, a))
            {
                committer.CommitNode(ref emptyPath, new NodeCommitInfo(a));
            }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(2, a)) { }
            using (ICommitter committer = fullTrieStore.BeginStateBlockCommit(3, a))
            {
                committer.CommitNode(ref emptyPath, new NodeCommitInfo(b)); // <- new root
            }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(4, b)) { } // should be 'a' to test properly
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(5, b)) { }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(6, b)) { }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(7, b)) { }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(8, b)) { }

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
            using TrieStore trieStore = TestTrieStoreFactory.Build(new BadDb(), _logManager);
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

            using (fullTrieStore.BeginStateBlockCommit(0, null)) { }

            using (fullTrieStore.BeginBlockCommit(1))
            {
                using (ICommitter committer = fullTrieStore.GetTrieStore(TestItem.KeccakA).BeginCommit(storage1))
                {
                    committer.CommitNode(ref emptyPath, new NodeCommitInfo(storage1));
                }

                using (ICommitter committer = fullTrieStore.GetTrieStore(null).BeginCommit(a))
                {
                    committer.CommitNode(ref emptyPath, new NodeCommitInfo(a));
                }
            }

            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(2, a)) { }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(3, a)) { }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(4, a)) { }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(5, a)) { }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(6, a)) { }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(7, a)) { }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(8, a)) { }

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

            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(0, null)) { }

            using (fullTrieStore.BeginBlockCommit(1))
            {
                using (ICommitter committer = fullTrieStore.GetTrieStore(null).BeginCommit(storage1))
                {
                    committer.CommitNode(ref emptyPath, new NodeCommitInfo(a));
                    committer.CommitNode(ref emptyPath, new NodeCommitInfo(storage1));
                }

                using (ICommitter _ = trieStore.BeginCommit(a)) { }

            }

            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(2, a)) { }
            using (ICommitter committer = fullTrieStore.BeginStateBlockCommit(2, b))
            {
                committer.CommitNode(ref emptyPath, new NodeCommitInfo(b)); // <- new root
            }

            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(4, b)) { } // Should be 'a' to test properly
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(5, b)) { } // Should be 'a' to test properly
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(6, b)) { } // Should be 'a' to test properly
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(7, b)) { } // Should be 'a' to test properly
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(8, b)) { } // Should be 'a' to test properly

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

            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(0, null)) { }

            using (fullTrieStore.BeginBlockCommit(1))
            {
                using (ICommitter committer = fullTrieStore.GetTrieStore(new Hash256(Nibbles.ToBytes(storage1Nib))).BeginCommit(storage1))
                {
                    committer.CommitNode(ref emptyPath, new NodeCommitInfo(storage1));
                }

                using (ICommitter committer = fullTrieStore.GetTrieStore(new Hash256(Nibbles.ToBytes(storage2Nib))).BeginCommit(storage2))
                {
                    committer.CommitNode(ref emptyPath, new NodeCommitInfo(storage2));
                }

                using (ICommitter committer = fullTrieStore.GetTrieStore(null).BeginCommit(branch))
                {
                    committer.CommitNode(ref emptyPath, new NodeCommitInfo(a));
                    committer.CommitNode(ref emptyPath, new NodeCommitInfo(b));
                    committer.CommitNode(ref emptyPath, new NodeCommitInfo(branch));
                }
            }

            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(2, branch)) { }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(3, branch)) { }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(4, branch)) { }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(5, branch)) { }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(6, branch)) { }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(7, branch)) { }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(8, branch)) { }

            storage.Get(null, TreePath.FromNibble(new byte[] { 0 }), a.Keccak).Should().NotBeNull();
            storage.Get(new Hash256(Nibbles.ToBytes(storage1Nib)), TreePath.Empty, storage1.Keccak).Should().NotBeNull();
            fullTrieStore.IsNodeCached(null, TreePath.Empty, a.Keccak).Should().BeTrue();
            fullTrieStore.IsNodeCached(new Hash256(Nibbles.ToBytes(storage1Nib)), TreePath.Empty, storage1.Keccak).Should().BeTrue();
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
            using (ICommitter? committer = fullTrieStore.BeginStateBlockCommit(0, trieNode))
            {
                committer.CommitNode(ref emptyPath, new NodeCommitInfo(trieNode));
            }

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

            using (ICommitter? committer = fullTrieStore.BeginStateBlockCommit(0, node))
            {
                committer.CommitNode(ref emptyPath, new NodeCommitInfo(node));
            }

            var originalNode = trieStore.FindCachedOrUnknown(TreePath.Empty, node.Keccak);

            IReadOnlyTrieStore readOnlyTrieStore = fullTrieStore.AsReadOnly();
            var readOnlyNode = readOnlyTrieStore.FindCachedOrUnknown(null, TreePath.Empty, node.Keccak);

            readOnlyNode.Should().NotBe(originalNode);
            readOnlyNode.Should().BeEquivalentTo(originalNode,
                static eq => eq.Including(static t => t.Keccak)
                    .Including(static t => t.NodeType));

            var origRlp = originalNode.FullRlp;
            var readOnlyRlp = readOnlyNode.FullRlp;
            readOnlyRlp.Should().BeEquivalentTo(origRlp);

            readOnlyNode.Key?.ToString().Should().Be(originalNode.Key?.ToString());
        }

        private long ExpectedPerNodeKeyMemorySize => (_scheme == INodeStorage.KeyScheme.Hash ? 0 : TrieStoreDirtyNodesCache.Key.MemoryUsage) + MemorySizes.ObjectHeaderMethodTable + MemorySizes.RefSize + 4 + MemorySizes.RefSize;

        [Test]
        public void After_commit_should_have_has_root()
        {
            MemDb db = new();
            TrieStore trieStore = TestTrieStoreFactory.Build(db, LimboLogs.Instance);
            trieStore.HasRoot(Keccak.EmptyTreeHash).Should().BeTrue();

            Account account = new(1);
            StateTree stateTree = new(trieStore, LimboLogs.Instance);
            stateTree.Set(TestItem.AddressA, account);
            stateTree.Commit();
            trieStore.HasRoot(stateTree.RootHash).Should().BeTrue();

            stateTree.Get(TestItem.AddressA);
            account = account.WithChangedBalance(2);
            stateTree.Set(TestItem.AddressA, account);
            stateTree.Commit();
            trieStore.HasRoot(stateTree.RootHash).Should().BeTrue();
        }

        [Test]
        [Retry(3)]
        public async Task Will_RemovePastKeys_OnSnapshot()
        {
            MemDb memDb = new();

            using TrieStore fullTrieStore = CreateTrieStore(
                kvStore: memDb,
                pruningStrategy: new TestPruningStrategy(true, true),
                persistenceStrategy: No.Persistence,
                pruningConfig: new PruningConfig()
                {
                    PruningBoundary = 2,
                    TrackPastKeys = true
                });

            TreePath emptyPath = TreePath.Empty;

            for (int i = 0; i < 64; i++)
            {
                TrieNode node = new(NodeType.Leaf, TestItem.Keccaks[i], new byte[2]);
                using (ICommitter? committer = fullTrieStore.BeginStateBlockCommit(i, node))
                {
                    committer.CommitNode(ref emptyPath, new NodeCommitInfo(node));
                }

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
        public async Task Will_Trigger_ReorgBoundaryEvent_On_Prune()
        {
            // TODO: Check why slow
            MemDb memDb = new();

            using TrieStore fullTrieStore = CreateTrieStore(
                kvStore: memDb,
                pruningStrategy: new TestPruningStrategy(true, true),
                persistenceStrategy: No.Persistence,
                pruningConfig: new PruningConfig()
                {
                    PruningBoundary = 2,
                    Enabled = true
                });

            long reorgBoundary = 0;
            fullTrieStore.ReorgBoundaryReached += (sender, reached) => reorgBoundary = reached.BlockNumber;

            IScopedTrieStore trieStore = fullTrieStore.GetTrieStore(null);
            TreePath emptyPath = TreePath.Empty;

            for (int i = 0; i < 64; i++)
            {
                TrieNode node = new(NodeType.Leaf, TestItem.Keccaks[i], new byte[2]);
                using (ICommitter? committer = fullTrieStore.BeginStateBlockCommit(i, node))
                {
                    committer.CommitNode(ref emptyPath, new NodeCommitInfo(node));
                }

                if (i > 4)
                {
                    Assert.That(() => reorgBoundary, Is.EqualTo(i - 3).After(10000, 100));
                }
                else
                {
                    // Pruning is done in background
                    await Task.Delay(TimeSpan.FromMilliseconds(1000));
                }
            }
        }

        [Test]
        public async Task Will_Not_RemovePastKeys_OnSnapshot_DuringFullPruning()
        {
            MemDb memDb = new();

            IPersistenceStrategy isPruningPersistenceStrategy = Substitute.For<IPersistenceStrategy>();
            isPruningPersistenceStrategy.IsFullPruning.Returns(true);

            using TrieStore fullTrieStore = CreateTrieStore(
                kvStore: memDb,
                pruningStrategy: new TestPruningStrategy(true, true),
                persistenceStrategy: isPruningPersistenceStrategy,
                pruningConfig: new PruningConfig()
                {
                    TrackPastKeys = true,
                    PruningBoundary = 2
                });

            TreePath emptyPath = TreePath.Empty;
            TaskCompletionSource tcs = new TaskCompletionSource();
            fullTrieStore.OnMemoryPruneCompleted += (sender, args) => tcs.TrySetResult();

            for (int i = 0; i < 64; i++)
            {
                TrieNode node = new(NodeType.Leaf, TestItem.Keccaks[i], new byte[2]);
                using (ICommitter? committer = fullTrieStore.BeginStateBlockCommit(i, node))
                {
                    committer.CommitNode(ref emptyPath, new NodeCommitInfo(node));
                }

                // Pruning is done in background
                await tcs.Task;
                tcs = new TaskCompletionSource();
            }

            memDb.Count.Should().Be(61);
            fullTrieStore.Prune();
            fullTrieStore.MemoryUsedByDirtyCache.Should().Be(_scheme == INodeStorage.KeyScheme.Hash ? 880 : 1088);
        }

        [Test]
        public async Task Will_NotRemove_ReCommittedNode()
        {
            MemDb memDb = new();

            using TrieStore fullTrieStore = CreateTrieStore(
                kvStore: memDb,
                pruningStrategy: new TestPruningStrategy(true, true),
                persistenceStrategy: No.Persistence,
                pruningConfig: new PruningConfig()
                {
                    PruningBoundary = 2,
                    TrackPastKeys = true
                });

            IScopedTrieStore trieStore = fullTrieStore.GetTrieStore(null);
            TreePath emptyPath = TreePath.Empty;

            for (int i = 0; i < 64; i++)
            {
                TrieNode node = new(NodeType.Leaf, TestItem.Keccaks[i % 4], new byte[2]);
                using (ICommitter committer = fullTrieStore.BeginStateBlockCommit(i, node))
                {
                    committer.CommitNode(ref emptyPath, new NodeCommitInfo(node));
                }

                // Pruning is done in background
                await Task.Delay(TimeSpan.FromMilliseconds(10));
            }

            memDb.Count.Should().Be(4);
        }

        [Test]
        public void When_SomeKindOfNonResolvedNotInMainWorldState_OnPrune_DoNotDeleteNode()
        {
            IDbProvider memDbProvider = TestMemDbProvider.Init();
            Address address = TestItem.AddressA;
            UInt256 slot = 1;

            INodeStorage nodeStorage = new NodeStorage(memDbProvider.StateDb, _scheme);
            (Hash256 stateRoot, ValueHash256 storageRoot) = SetupStartingState();
            nodeStorage.Get(address.ToAccountPath.ToCommitment(), TreePath.Empty, storageRoot).Should().NotBeNull();

            using TrieStore fullTrieStore = CreateTrieStore(
                kvStore: memDbProvider.StateDb,
                pruningStrategy: new TestPruningStrategy(true, true),
                persistenceStrategy: No.Persistence,
                pruningConfig: new PruningConfig()
                {
                    PruningBoundary = 2,
                    TrackPastKeys = true
                });

            WorldState worldState = new WorldState(
                fullTrieStore,
                memDbProvider.CodeDb,
                LimboLogs.Instance);

            // Simulate some kind of cache access which causes unresolved node to remain.
            IScopedTrieStore storageTrieStore = fullTrieStore.GetTrieStore(address);
            storageTrieStore.FindCachedOrUnknown(TreePath.Empty, storageRoot.ToCommitment());

            worldState.StateRoot = stateRoot;
            worldState.IncrementNonce(address, 1);
            worldState.Commit(MainnetSpecProvider.Instance.GenesisSpec);
            worldState.CommitTree(2);

            fullTrieStore.PersistCache(default);
            nodeStorage.Get(address.ToAccountPath.ToCommitment(), TreePath.Empty, storageRoot).Should().NotBeNull();

            return;

            (Hash256, ValueHash256) SetupStartingState()
            {
                WorldState worldState = new WorldState(TestTrieStoreFactory.Build(nodeStorage, LimboLogs.Instance), memDbProvider.CodeDb, LimboLogs.Instance);
                worldState.StateRoot = Keccak.EmptyTreeHash;
                worldState.CreateAccountIfNotExists(address, UInt256.One);
                worldState.Set(new StorageCell(address, slot), TestItem.KeccakB.BytesToArray());
                worldState.Commit(MainnetSpecProvider.Instance.GenesisSpec);
                worldState.CommitTree(1);

                ValueHash256 storageRoot = worldState.GetStorageRoot(address);
                Hash256 stateRoot = worldState.StateRoot;
                return (stateRoot, storageRoot);
            }

        }

        [Test]
        public async Task When_Prune_ClearRecommittedPersistedNode()
        {
            MemDb memDb = new();

            IPersistenceStrategy isPruningPersistenceStrategy = Substitute.For<IPersistenceStrategy>();

            using TrieStore fullTrieStore = CreateTrieStore(
                kvStore: memDb,
                pruningStrategy: new TestPruningStrategy(true, true),
                persistenceStrategy: isPruningPersistenceStrategy,
                pruningConfig: new PruningConfig()
                {
                    PruningBoundary = 64,
                    TrackPastKeys = true
                });

            TreePath emptyPath = TreePath.Empty;
            TaskCompletionSource tcs = new TaskCompletionSource();
            fullTrieStore.OnMemoryPruneCompleted += (sender, args) => tcs.TrySetResult();

            for (int i = 0; i < 64; i++)
            {
                TrieNode node = new(NodeType.Leaf, TestItem.Keccaks[i], new byte[2]);
                using (ICommitter? committer = fullTrieStore.BeginStateBlockCommit(i, node))
                {
                    committer.CommitNode(ref emptyPath, new NodeCommitInfo(node));
                }

                // Pruning is done in background
                await tcs.Task;
                tcs = new TaskCompletionSource();
            }

            memDb.Count.Should().Be(0);
            fullTrieStore.MemoryUsedByDirtyCache.Should().Be(_scheme == INodeStorage.KeyScheme.Hash ? 14080 : 17408);

            fullTrieStore.PersistCache(default);
            memDb.Count.Should().Be(64);
            fullTrieStore.MemoryUsedByDirtyCache.Should().Be(0);
        }
    }
}
