// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Db;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Evm.State;
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
                committer.CommitNode(ref emptyPath, trieNode);
            }
            fullTrieStore.MemoryUsedByDirtyCache.Should().Be(
                trieNode.GetMemorySize(false) + ExpectedPerNodeKeyMemorySize);
        }

        [Test]
        public async Task Flush_ShouldBeCalledOnEachPersist()
        {
            TrieNode trieNode = new(NodeType.Leaf, Keccak.Zero);

            TestMemDb testMemDb = new TestMemDb();
            using TrieStore fullTrieStore = CreateTrieStore(persistenceStrategy: Archive.Instance, pruningStrategy: new TestPruningStrategy(shouldPrune: true), kvStore: testMemDb, pruningConfig: new PruningConfig()
            {
                PruningBoundary = 0
            });
            PatriciaTree pt = new PatriciaTree(fullTrieStore.GetTrieStore(null), LimboLogs.Instance);

            BlockHeader? baseBlock = null;
            for (int i = 0; i < 4; i++)
            {
                using (var _ = fullTrieStore.BeginScope(baseBlock))
                {
                    pt.Set(TestItem.KeccakA.BytesToArray(), TestItem.Keccaks[i].BytesToArray());
                    using (fullTrieStore.BeginStateBlockCommit(i + 1, trieNode))
                    {
                        pt.Commit();
                    }

                    baseBlock = Build.A.BlockHeader.WithParentOptional(baseBlock).WithStateRoot(pt.RootHash).TestObject;
                }
                await Task.Yield();
                fullTrieStore.WaitForPruning();
            }

            testMemDb.FlushCount.Should().Be(4);
        }

        [Test]
        public void Pruning_off_cache_should_change_commit_node()
        {
            TrieNode trieNode = new(NodeType.Leaf, Keccak.Zero);
            TrieNode trieNode2 = new(NodeType.Branch, TestItem.KeccakA);
            TrieNode trieNode3 = new(NodeType.Branch, TestItem.KeccakB);

            using TrieStore fullTrieStore = CreateTrieStore();
            TreePath emptyPath = TreePath.Empty;
            using (ICommitter? committer = fullTrieStore.BeginStateBlockCommit(1234, trieNode))
            {
                committer.CommitNode(ref emptyPath, trieNode);
            }
            using (ICommitter? committer = fullTrieStore.BeginStateBlockCommit(1235, trieNode))
            {
                committer.CommitNode(ref emptyPath, trieNode2);
                committer.CommitNode(ref emptyPath, trieNode3);
            }
            fullTrieStore.WaitForPruning();
            fullTrieStore.MemoryUsedByDirtyCache.Should().Be(_scheme == INodeStorage.KeyScheme.HalfPath ? 832 : 676);
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
        public void Pruning_off_cache_should_find_cached_or_unknown()
        {
            using TrieStore trieStore = CreateTrieStore();
            TrieNode returnedNode = trieStore.FindCachedOrUnknown(null, TreePath.Empty, TestItem.KeccakA);
            TrieNode returnedNode2 = trieStore.FindCachedOrUnknown(null, TreePath.Empty, TestItem.KeccakB);
            TrieNode returnedNode3 = trieStore.FindCachedOrUnknown(null, TreePath.Empty, TestItem.KeccakC);
            Assert.That(returnedNode.NodeType, Is.EqualTo(NodeType.Unknown));
            Assert.That(returnedNode2.NodeType, Is.EqualTo(NodeType.Unknown));
            Assert.That(returnedNode3.NodeType, Is.EqualTo(NodeType.Unknown));
            trieStore.WaitForPruning();
            trieStore.MemoryUsedByDirtyCache.Should().Be(_scheme == INodeStorage.KeyScheme.HalfPath ? 552 : 396);
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
                committer.CommitNode(ref emptyPath, trieNode1);
                committer.CommitNode(ref emptyPath, trieNode2);
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

            fullTrieStore.MemoryUsedByDirtyCache.Should().Be(_scheme == INodeStorage.KeyScheme.Hash ? 545956 : 616104L);
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
                committer.CommitNode(ref emptyPath, trieNode1);
                committer.CommitNode(ref emptyPath, trieNode2);
            }

            using (ICommitter? committer = fullTrieStore.BeginStateBlockCommit(1235, trieNode2))
            {
                committer.CommitNode(ref emptyPath, trieNode3);
                committer.CommitNode(ref emptyPath, trieNode4);
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
            trieNode1.ResolveKey(null!, ref emptyPath);
            TrieNode trieNode2 = new(NodeType.Leaf, new byte[1]);
            trieNode2.ResolveKey(null!, ref emptyPath);

            TrieNode trieNode3 = new(NodeType.Leaf, new byte[2]);
            trieNode3.ResolveKey(null!, ref emptyPath);

            TrieNode trieNode4 = new(NodeType.Leaf, new byte[3]);
            trieNode4.ResolveKey(null!, ref emptyPath);

            using TrieStore fullTrieStore = CreateTrieStore(pruningStrategy: new MemoryLimit(640));

            using (ICommitter committer = fullTrieStore.BeginStateBlockCommit(1234, trieNode2))
            {
                committer.CommitNode(ref emptyPath, trieNode1);
                committer.CommitNode(ref emptyPath, trieNode2);
            }

            using (ICommitter committer = fullTrieStore.BeginStateBlockCommit(1235, trieNode2))
            {
                committer.CommitNode(ref emptyPath, trieNode3);
                committer.CommitNode(ref emptyPath, trieNode4);
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
            trieNode1.ResolveKey(null!, ref emptyPath);
            TrieNode trieNode2 = new(NodeType.Leaf, new byte[1]);
            trieNode2.ResolveKey(null!, ref emptyPath);

            TrieNode trieNode3 = new(NodeType.Leaf, new byte[2]);
            trieNode3.ResolveKey(null!, ref emptyPath);

            TrieNode trieNode4 = new(NodeType.Leaf, new byte[3]);
            trieNode4.ResolveKey(null!, ref emptyPath);

            using TrieStore fullTrieStore = CreateTrieStore(pruningStrategy: new MemoryLimit(512));

            using (ICommitter committer = fullTrieStore.BeginStateBlockCommit(1234, trieNode2))
            {
                committer.CommitNode(ref emptyPath, trieNode1);
                committer.CommitNode(ref emptyPath, trieNode2);
            }

            using (ICommitter committer = fullTrieStore.BeginStateBlockCommit(1235, trieNode2))
            {
                committer.CommitNode(ref emptyPath, trieNode3);
                committer.CommitNode(ref emptyPath, trieNode4);
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
                fakeRoot.ResolveKey(NullTrieNodeResolver.Instance, ref emptyPath);
                using (ICommitter committer = fullTrieStore.BeginStateBlockCommit(i, fakeRoot))
                {
                    for (int j = 0; j < 1 + i % 3; j++)
                    {
                        TrieNode trieNode = new(NodeType.Leaf, []); // 192B
                        trieNode.ResolveKey(NullTrieNodeResolver.Instance, ref emptyPath);
                        committer.CommitNode(ref emptyPath, trieNode);
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
            a.ResolveKey(NullTrieNodeResolver.Instance, ref emptyPath);

            MemDb memDb = new();
            NodeStorage storage = new NodeStorage(memDb);

            using TrieStore fullTrieStore = CreateTrieStore(
                pruningStrategy: new MemoryLimit(16.MB()).WhenLastPersistedBlockIsTooOld(4, 0),
                kvStore: memDb,
                persistenceStrategy: new ConstantInterval(4),
                new PruningConfig()
                {
                    PruningBoundary = 4,
                });

            using (ICommitter committer = fullTrieStore.BeginStateBlockCommit(0, a))
            {
                committer.CommitNode(ref emptyPath, a);
            }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(1, a)) { }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(2, a)) { }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(3, a)) { }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(4, a)) { }

            fullTrieStore.WaitForPruning();
            storage.Get(null, TreePath.Empty, a.Keccak).Should().NotBeNull();
            fullTrieStore.IsNodeCached(null, TreePath.Empty, a.Keccak).Should().BeTrue();
        }

        [Test]
        public void Stays_in_memory_until_persisted()
        {
            TrieNode a = new(NodeType.Leaf, []); // 192B
            TreePath emptyPath = TreePath.Empty;
            a.ResolveKey(NullTrieNodeResolver.Instance, ref emptyPath);

            MemDb memDb = new();
            NodeStorage storage = new NodeStorage(memDb);

            using TrieStore fullTrieStore = CreateTrieStore(pruningStrategy: new MemoryLimit(16.MB()));

            using (ICommitter committer = fullTrieStore.BeginStateBlockCommit(0, a))
            {
                committer.CommitNode(ref emptyPath, a);
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
            a.ResolveKey(NullTrieNodeResolver.Instance, ref emptyPath);

            MemDb memDb = new();
            NodeStorage storage = new NodeStorage(memDb);

            using TrieStore fullTrieStore = CreateTrieStore(
                kvStore: memDb,
                pruningStrategy: new TestPruningStrategy(true, true)
                {
                    ShouldPrunePersistedEnabled = false,
                }.WhenLastPersistedBlockIsTooOld(4, 0),
                persistenceStrategy: new ConstantInterval(4),
                pruningConfig: new PruningConfig()
                {
                    PruningBoundary = 0,
                }
            );

            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(0, null)) { }
            using (ICommitter committer = fullTrieStore.BeginStateBlockCommit(1, a))
            {
                committer.CommitNode(ref emptyPath, a);
            }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(2, a)) { }
            fullTrieStore.WaitForPruning();
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(3, a)) { }
            fullTrieStore.WaitForPruning();
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(4, a)) { }
            fullTrieStore.WaitForPruning();
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(5, a)) { }
            fullTrieStore.WaitForPruning();
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(6, a)) { }
            fullTrieStore.WaitForPruning();
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(7, a)) { }
            fullTrieStore.WaitForPruning();
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(8, a)) { }
            fullTrieStore.WaitForPruning();

            storage.Get(null, TreePath.Empty, a.Keccak).Should().NotBeNull();
            fullTrieStore.IsNodeCached(null, TreePath.Empty, a.Keccak).Should().BeTrue();
        }

        [Test]
        public void Will_not_get_dropped_on_snapshot_if_unreferenced_in_later_blocks()
        {
            TrieNode a = new(NodeType.Leaf, []);
            TreePath emptyPath = TreePath.Empty;
            a.ResolveKey(NullTrieNodeResolver.Instance, ref emptyPath);

            TrieNode b = new(NodeType.Leaf, new byte[1]);
            b.ResolveKey(NullTrieNodeResolver.Instance, ref emptyPath);

            MemDb memDb = new();
            NodeStorage nodeStorage = new NodeStorage(memDb);

            using TrieStore fullTrieStore = CreateTrieStore(
                kvStore: memDb,
                pruningStrategy: new MemoryLimit(16.MB()).WhenLastPersistedBlockIsTooOld(4, 0),
                persistenceStrategy: new ConstantInterval(4),
                pruningConfig: new PruningConfig()
                {
                    PruningBoundary = 0
                });

            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(0, null)) { }
            using (ICommitter committer = fullTrieStore.BeginStateBlockCommit(1, a))
            {
                committer.CommitNode(ref emptyPath, a);
            }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(2, a)) { }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(3, a)) { }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(4, a)) { }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(5, a)) { }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(6, a)) { }
            using (ICommitter committer = fullTrieStore.BeginStateBlockCommit(7, a))
            {
                committer.CommitNode(ref emptyPath, b);
            }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(8, a)) { }

            fullTrieStore.WaitForPruning();
            nodeStorage.Get(null, TreePath.Empty, a.Keccak).Should().NotBeNull();
            fullTrieStore.IsNodeCached(null, TreePath.Empty, a.Keccak).Should().BeTrue();
        }

        [Test]
        public void Will_get_dropped_on_snapshot_if_it_was_a_transient_node()
        {
            TrieNode a = new(NodeType.Leaf, new byte[] { 1 });
            TreePath emptyPath = TreePath.Empty;
            a.ResolveKey(NullTrieNodeResolver.Instance, ref emptyPath);

            TrieNode b = new(NodeType.Leaf, new byte[] { 2 });
            b.ResolveKey(NullTrieNodeResolver.Instance, ref emptyPath);

            MemDb memDb = new();

            using TrieStore fullTrieStore = CreateTrieStore(
                kvStore: memDb,
                pruningStrategy: new MemoryLimit(16.MB()),
                persistenceStrategy: new ConstantInterval(4));

            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(0, null)) { }
            using (ICommitter committer = fullTrieStore.BeginStateBlockCommit(1, a))
            {
                committer.CommitNode(ref emptyPath, a);
            }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(2, a)) { }
            using (ICommitter committer = fullTrieStore.BeginStateBlockCommit(3, a))
            {
                committer.CommitNode(ref emptyPath, b); // <- new root
            }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(4, b)) { } // should be 'a' to test properly
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(5, b)) { }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(6, b)) { }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(7, b)) { }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(8, b)) { }

            memDb[a.Keccak!.Bytes].Should().BeNull();
            fullTrieStore.IsNodeCached(null, TreePath.Empty, a.Keccak).Should().BeTrue();
        }

        [Test]
        public void Trie_store_multi_threaded_scenario()
        {
            IWorldState worldState = TestWorldStateFactory.CreateForTest();
            using var _ = worldState.BeginScope(IWorldState.PreGenesis);
            worldState.CreateAccount(TestItem.AddressA, 1000);
            worldState.CreateAccount(TestItem.AddressB, 1000);
        }

        [Test]
        public void Will_store_storage_on_snapshot()
        {
            TrieNode storage1 = new(NodeType.Leaf, new byte[2]);
            TreePath emptyPath = TreePath.Empty;
            storage1.ResolveKey(NullTrieNodeResolver.Instance, ref emptyPath);

            TrieNode a = new(NodeType.Leaf);
            Account account = new(1, 1, storage1.Keccak, Keccak.OfAnEmptyString);
            a.Value = _accountDecoder.Encode(account).Bytes;
            a.Key = Nibbles.BytesToNibbleBytes(TestItem.KeccakA.BytesToArray());
            a.ResolveKey(NullTrieNodeResolver.Instance, ref emptyPath);

            MemDb memDb = new();
            NodeStorage asStorage = new NodeStorage(memDb);

            using TrieStore fullTrieStore = CreateTrieStore(
                kvStore: memDb,
                pruningStrategy: new MemoryLimit(16.MB()).WhenLastPersistedBlockIsTooOld(4, 0),
                persistenceStrategy: new ConstantInterval(4),
                pruningConfig: new PruningConfig()
                {
                    PruningBoundary = 0
                });

            using (fullTrieStore.BeginStateBlockCommit(0, null)) { }

            using (fullTrieStore.BeginBlockCommit(1))
            {
                using (ICommitter committer = fullTrieStore.GetTrieStore(TestItem.KeccakA).BeginCommit(storage1))
                {
                    committer.CommitNode(ref emptyPath, storage1);
                }

                using (ICommitter committer = fullTrieStore.GetTrieStore(null).BeginCommit(a))
                {
                    committer.CommitNode(ref emptyPath, a);
                }
            }

            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(2, a)) { }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(3, a)) { }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(4, a)) { }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(5, a)) { }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(6, a)) { }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(7, a)) { }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(8, a)) { }

            fullTrieStore.WaitForPruning();
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
            storage1.ResolveKey(NullTrieNodeResolver.Instance, ref emptyPath);

            TrieNode a = new(NodeType.Leaf);
            Account account = new(1, 1, storage1.Keccak, Keccak.OfAnEmptyString);
            a.Value = _accountDecoder.Encode(account).Bytes;
            a.Key = Bytes.FromHexString("abc");
            a.ResolveKey(NullTrieNodeResolver.Instance, ref emptyPath);

            TrieNode b = new(NodeType.Leaf, new byte[1]);
            b.ResolveKey(NullTrieNodeResolver.Instance, ref emptyPath);

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
                    committer.CommitNode(ref emptyPath, a);
                    committer.CommitNode(ref emptyPath, storage1);
                }

                using (ICommitter _ = trieStore.BeginCommit(a)) { }

            }

            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(2, a)) { }
            using (ICommitter committer = fullTrieStore.BeginStateBlockCommit(2, b))
            {
                committer.CommitNode(ref emptyPath, b); // <- new root
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
            storage1.ResolveKey(NullTrieNodeResolver.Instance, ref emptyPath);

            TrieNode a = new(NodeType.Leaf);
            Account account = new(1, 1, storage1.Keccak, Keccak.OfAnEmptyString);
            a.Value = _accountDecoder.Encode(account).Bytes;
            a.Key = storage1Nib[1..];
            a.ResolveKey(NullTrieNodeResolver.Instance, ref emptyPath);

            TrieNode storage2 = new(NodeType.Leaf, new byte[32]);
            storage2.ResolveKey(NullTrieNodeResolver.Instance, ref emptyPath);

            TrieNode b = new(NodeType.Leaf);
            Account accountB = new(2, 1, storage2.Keccak, Keccak.OfAnEmptyString);
            b.Value = _accountDecoder.Encode(accountB).Bytes;
            b.Key = storage2Nib[1..];
            b.ResolveKey(NullTrieNodeResolver.Instance, ref emptyPath);

            TrieNode branch = new(NodeType.Branch);
            branch.SetChild(0, a);
            branch.SetChild(1, b);
            branch.ResolveKey(NullTrieStore.Instance, ref emptyPath);

            MemDb memDb = new();
            NodeStorage storage = new NodeStorage(memDb);

            using TrieStore fullTrieStore = CreateTrieStore(
                kvStore: memDb,
                pruningStrategy: new MemoryLimit(16.MB()).WhenLastPersistedBlockIsTooOld(4, 0),
                persistenceStrategy: new ConstantInterval(4),
                pruningConfig: new PruningConfig()
                {
                    PruningBoundary = 0
                });

            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(0, null)) { }

            using (fullTrieStore.BeginBlockCommit(1))
            {
                using (ICommitter committer = fullTrieStore.GetTrieStore(new Hash256(Nibbles.ToBytes(storage1Nib))).BeginCommit(storage1))
                {
                    committer.CommitNode(ref emptyPath, storage1);
                }

                using (ICommitter committer = fullTrieStore.GetTrieStore(new Hash256(Nibbles.ToBytes(storage2Nib))).BeginCommit(storage2))
                {
                    committer.CommitNode(ref emptyPath, storage2);
                }

                using (ICommitter committer = fullTrieStore.GetTrieStore(null).BeginCommit(branch))
                {
                    committer.CommitNode(ref emptyPath, a);
                    committer.CommitNode(ref emptyPath, b);
                    committer.CommitNode(ref emptyPath, branch);
                }
            }

            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(2, branch)) { }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(3, branch)) { }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(4, branch)) { }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(5, branch)) { }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(6, branch)) { }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(7, branch)) { }
            using (ICommitter _ = fullTrieStore.BeginStateBlockCommit(8, branch)) { }

            fullTrieStore.WaitForPruning();
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
            trieNode.ResolveKey(trieStore, ref emptyPath);
            using (ICommitter? committer = fullTrieStore.BeginStateBlockCommit(0, trieNode))
            {
                committer.CommitNode(ref emptyPath, trieNode);
            }
            using (fullTrieStore.PrepareStableState(default)) { }

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
            node.ResolveKey(NullTrieNodeResolver.Instance, ref emptyPath);

            using TrieStore fullTrieStore = CreateTrieStore(pruningStrategy: new TestPruningStrategy(pruning));
            IScopedTrieStore trieStore = fullTrieStore.GetTrieStore(null);

            using (ICommitter? committer = fullTrieStore.BeginStateBlockCommit(0, node))
            {
                committer.CommitNode(ref emptyPath, node);
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
            TrieStore trieStore = CreateTrieStore(kvStore: db);
            trieStore.HasRoot(Keccak.EmptyTreeHash).Should().BeTrue();
            StateTree stateTree = new(trieStore, LimboLogs.Instance);

            Account account = new(1);
            {
                using var _ = trieStore.BeginBlockCommit(0);
                stateTree.Set(TestItem.AddressA, account);
                stateTree.Commit();
            }
            trieStore.HasRoot(stateTree.RootHash).Should().BeTrue();

            stateTree.Get(TestItem.AddressA);
            account = account.WithChangedBalance(2);

            {
                using var _ = trieStore.BeginBlockCommit(0);
                stateTree.Set(TestItem.AddressA, account);
                stateTree.Commit();
            }
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
                TrieNode node = new(NodeType.Leaf, TestItem.Keccaks[i], new SpanSource(new byte[2]));
                using (ICommitter? committer = fullTrieStore.BeginStateBlockCommit(i, node))
                {
                    committer.CommitNode(ref emptyPath, node);
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
                    PruningBoundary = 3,
                    Enabled = true
                });

            long reorgBoundary = 0;
            fullTrieStore.ReorgBoundaryReached += (sender, reached) => reorgBoundary = reached.BlockNumber;

            IScopedTrieStore trieStore = fullTrieStore.GetTrieStore(null);
            TreePath emptyPath = TreePath.Empty;

            for (int i = 0; i < 64; i++)
            {
                TrieNode node = new(NodeType.Leaf, TestItem.Keccaks[i], new SpanSource(new byte[2]));
                using (ICommitter? committer = fullTrieStore.BeginStateBlockCommit(i, node))
                {
                    committer.CommitNode(ref emptyPath, node);
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
        public async Task Will_NotRemove_ReCommittedNode()
        {
            MemDb memDb = new();

            using TrieStore fullTrieStore = CreateTrieStore(
                kvStore: memDb,
                pruningStrategy: new TestPruningStrategy(true, true),
                persistenceStrategy: No.Persistence,
                pruningConfig: new PruningConfig()
                {
                    PruningBoundary = 3,
                    TrackPastKeys = true
                });

            TreePath emptyPath = TreePath.Empty;

            for (int i = 0; i < 64; i++)
            {
                TrieNode node = new(NodeType.Leaf, TestItem.Keccaks[i % 4], new SpanSource(new byte[2]));
                using (ICommitter committer = fullTrieStore.BeginStateBlockCommit(i, node))
                {
                    committer.CommitNode(ref emptyPath, node);
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

            using (worldState.BeginScope(Build.A.BlockHeader.WithNumber(1).WithStateRoot(stateRoot).TestObject))
            {
                worldState.IncrementNonce(address, 1);
                worldState.Commit(MainnetSpecProvider.Instance.GenesisSpec);
                worldState.CommitTree(2);
            }

            fullTrieStore.PersistCache(default);
            nodeStorage.Get(address.ToAccountPath.ToCommitment(), TreePath.Empty, storageRoot).Should().NotBeNull();

            return;

            (Hash256, ValueHash256) SetupStartingState()
            {
                WorldState worldState = new WorldState(new TestRawTrieStore(nodeStorage), memDbProvider.CodeDb, LimboLogs.Instance);
                using var _ = worldState.BeginScope(IWorldState.PreGenesis);
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
        public Task When_Prune_ClearRecommittedPersistedNode()
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

            for (int i = 0; i < 64; i++)
            {
                TrieNode node = new(NodeType.Leaf, TestItem.Keccaks[i], new SpanSource(new byte[2]));
                using (ICommitter? committer = fullTrieStore.BeginStateBlockCommit(i, node))
                {
                    committer.CommitNode(ref emptyPath, node);
                }

                // Pruning is done in background
                fullTrieStore.WaitForPruning();
            }

            memDb.Count.Should().Be(1);
            fullTrieStore.MemoryUsedByDirtyCache.Should().Be(_scheme == INodeStorage.KeyScheme.Hash ? 12032 : 15360);

            fullTrieStore.PersistCache(default);
            memDb.Count.Should().Be(64);
            fullTrieStore.MemoryUsedByDirtyCache.Should().Be(0);
            return Task.CompletedTask;
        }

        [Test]
        public void OnDispose_PersistAtLeastOneCommitSet()
        {
            MemDb memDb = new();

            TrieStore fullTrieStore = CreateTrieStore(
                kvStore: memDb,
                pruningStrategy: new TestPruningStrategy(false, false),
                persistenceStrategy: No.Persistence,
                pruningConfig: new PruningConfig()
                {
                    PruningBoundary = 5,
                    TrackPastKeys = true
                });

            TreePath emptyPath = TreePath.Empty;

            for (int i = 0; i < 2; i++)
            {
                TrieNode node = new(NodeType.Leaf, TestItem.Keccaks[i % 4], new SpanSource(new byte[2]));
                using (ICommitter committer = fullTrieStore.BeginStateBlockCommit(i + 1, node))
                {
                    committer.CommitNode(ref emptyPath, node);
                }
            }

            fullTrieStore.Dispose();
            memDb.Count.Should().Be(1);
        }

        [Test]
        public void Will_NotPruneTopLevelNode()
        {
            if (_scheme == INodeStorage.KeyScheme.Hash) Assert.Ignore("Not applicable for hash");

            MemDb memDb = new();
            TestPruningStrategy testPruningStrategy = new TestPruningStrategy(
                shouldPrune: false,
                deleteObsoleteKeys: true
            );

            TrieStore fullTrieStore = CreateTrieStore(
                kvStore: memDb,
                pruningStrategy: testPruningStrategy,
                persistenceStrategy: No.Persistence,
                pruningConfig: new PruningConfig()
                {
                    PruningBoundary = 4,
                    PrunePersistedNodePortion = 1.0,
                    DirtyNodeShardBit = 4,
                    MaxBufferedCommitCount = 0,
                    TrackPastKeys = true
                });

            PatriciaTree ptree = new PatriciaTree(fullTrieStore.GetTrieStore(null), LimboLogs.Instance);

            void WriteRandomData(int seed)
            {
                ptree.Set(Keccak.Compute(seed.ToBigEndianByteArray()).Bytes, Keccak.Compute(seed.ToBigEndianByteArray()).BytesToArray());
                ptree.Commit();
            }

            for (int i = 0; i < 10; i++)
            {
                using (fullTrieStore.BeginBlockCommit(i))
                {
                    if (i == 0)
                    {
                        ptree.Set(Keccak.Compute(10000.ToBigEndianByteArray()).Bytes, Keccak.Compute(i.ToBigEndianByteArray()).BytesToArray());
                    }
                    WriteRandomData(i);
                }
            }
            fullTrieStore.PersistAndPruneDirtyCache();

            for (int i = 10; i < 15; i++)
            {
                using (fullTrieStore.BeginBlockCommit(i))
                {
                    WriteRandomData(i);
                }
            }
            // Do a branch
            for (int i = 10; i < 15; i++)
            {
                using (fullTrieStore.BeginBlockCommit(i))
                {
                    WriteRandomData(i * 10);
                }
            }
            fullTrieStore.PersistAndPruneDirtyCache();

            for (int i = 15; i < 20; i++)
            {
                using (fullTrieStore.BeginBlockCommit(i))
                {
                    WriteRandomData(i);
                }
            }
            fullTrieStore.WaitForPruning();

            fullTrieStore.PrunePersistedNodes();
            fullTrieStore.CachedNodesCount.Should().Be(52);

            fullTrieStore.PersistAndPruneDirtyCache();
            fullTrieStore.PrunePersistedNodes();
            fullTrieStore.CachedNodesCount.Should().Be(20);
        }

        [TestCase(27, 1000, 31, 7)]
        [TestCase(27, 1000, 2, 2)]
        public void Will_HaveConsistentState_AfterPrune(int possibleSeed, int totalBlock, int snapshotInterval, int prunePersistedInterval)
        {
            MemDb memDb = new MemDb(writeDelay: 5, readDelay: 0);
            TestPruningStrategy testPruningStrategy = new TestPruningStrategy(
                shouldPrune: false,
                deleteObsoleteKeys: true
            );

            TrieStore fullTrieStore = CreateTrieStore(
                kvStore: memDb,
                pruningStrategy: testPruningStrategy,
                persistenceStrategy: No.Persistence,
                pruningConfig: new PruningConfig()
                {
                    PruningBoundary = 4,
                    PrunePersistedNodePortion = 0.1,
                    DirtyNodeShardBit = 8, // More shard this time
                    MaxBufferedCommitCount = 20,
                    PrunePersistedNodeMinimumTarget = 0,
                    TrackPastKeys = true
                });

            PatriciaTree ptree = new PatriciaTree(fullTrieStore.GetTrieStore(null), LimboLogs.Instance);

            void WriteRandomData(int seed)
            {
                ptree.Set(Keccak.Compute(seed.ToBigEndianByteArray()).Bytes, Keccak.Compute(seed.ToBigEndianByteArray()).BytesToArray());
                ptree.Set(Keccak.Compute((seed * 10000).ToBigEndianByteArray()).Bytes, Keccak.Compute(seed.ToBigEndianByteArray()).BytesToArray());
                ptree.Set(TestItem.KeccakA.Bytes, Keccak.Compute(seed.ToBigEndianByteArray()).BytesToArray());
                ptree.Commit();
            }

            HashSet<Hash256> rootsToTests = new HashSet<Hash256>();
            void VerifyAllTrie()
            {
                PatriciaTree readOnlyPTree = new PatriciaTree(fullTrieStore.AsReadOnly().GetTrieStore(null), LimboLogs.Instance);
                MemDb stubCodeDb = new MemDb();
                foreach (Hash256 rootsToTest in rootsToTests)
                {
                    if (!fullTrieStore.HasRoot(rootsToTest)) continue;
                    TrieStatsCollector collector = new TrieStatsCollector(stubCodeDb, LimboLogs.Instance, expectAccounts: false);
                    ptree.Accept(collector, rootHash: rootsToTest);
                    collector.Stats.MissingNodes.Should().Be(0);

                    collector = new TrieStatsCollector(stubCodeDb, LimboLogs.Instance, expectAccounts: false);
                    readOnlyPTree.Accept(collector, rootHash: rootsToTest);
                    collector.Stats.MissingNodes.Should().Be(0);
                }
            }

            BlockHeader baseBlock = Build.A.BlockHeader.WithStateRoot(Keccak.EmptyTreeHash).TestObject;
            for (int i = 0; i < totalBlock; i++)
            {
                int seed = i % possibleSeed;
                Hash256 parentRoot = ptree.RootHash ?? Keccak.EmptyTreeHash;
                using (fullTrieStore.BeginScope(baseBlock))
                {
                    using (fullTrieStore.BeginBlockCommit(i))
                    {
                        ptree.RootHash = parentRoot;
                        WriteRandomData(seed);
                        rootsToTests.Add(ptree.RootHash);
                    }
                }

                // Branches sometimes
                if ((i / 20) % 2 == 0)
                {
                    using (fullTrieStore.BeginScope(baseBlock))
                    {
                        using (fullTrieStore.BeginBlockCommit(i))
                        {
                            ptree.RootHash = parentRoot;
                            WriteRandomData(seed * 10);
                            rootsToTests.Add(ptree.RootHash);
                        }
                    }
                }

                // Persist sometimes
                testPruningStrategy.ShouldPruneEnabled = i % snapshotInterval == 0;
                testPruningStrategy.ShouldPrunePersistedEnabled = i % prunePersistedInterval == 0;
                fullTrieStore.SyncPruneCheck();
                testPruningStrategy.ShouldPruneEnabled = false;
                testPruningStrategy.ShouldPrunePersistedEnabled = false;

                VerifyAllTrie();
                baseBlock = Build.A.BlockHeader.WithParent(baseBlock).WithStateRoot(ptree.RootHash).TestObject;
            }
        }

        [Test]
        public async Task Will_Persist_ReCommittedPersistedNode_FromCommitBuffer()
        {
            int pruningBoundary = 4;

            ManualResetEvent writeBlocker = new ManualResetEvent(true);
            TestMemDb memDb = new();
            memDb.WriteFunc = (k, v) =>
            {
                writeBlocker.WaitOne();
                return true;
            };
            TestPruningStrategy testPruningStrategy = new TestPruningStrategy(
                shouldPrune: false,
                deleteObsoleteKeys: true
            );

            TrieStore fullTrieStore = CreateTrieStore(
                kvStore: memDb,
                pruningStrategy: testPruningStrategy,
                persistenceStrategy: No.Persistence,
                pruningConfig: new PruningConfig()
                {
                    PruningBoundary = pruningBoundary,
                    PrunePersistedNodePortion = 1.0,
                    DirtyNodeShardBit = 4,
                    MaxBufferedCommitCount = 1,
                    TrackPastKeys = true
                });

            PatriciaTree ptree = new PatriciaTree(fullTrieStore.GetTrieStore(null), LimboLogs.Instance);

            void WriteRandomData(int seed)
            {
                ptree.Set(TestItem.KeccakA.Bytes, Keccak.Compute(seed.ToBigEndianByteArray()).BytesToArray());
                ptree.Commit();
            }

            Hash256 persistedRootHash = null;
            int persistedBlockNumber = 5;

            for (int i = 0; i < 10; i++)
            {
                using (fullTrieStore.BeginBlockCommit(i))
                {
                    if (i == 0)
                    {
                        ptree.Set(Keccak.Compute(10000.ToBigEndianByteArray()).Bytes, Keccak.Compute(i.ToBigEndianByteArray()).BytesToArray());
                    }
                    WriteRandomData(i);

                    if (i == persistedBlockNumber)
                    {
                        persistedRootHash = ptree.RootHash;
                    }
                }
            }

            // Persisted nodes should be from block 5
            fullTrieStore.PersistAndPruneDirtyCache();
            fullTrieStore.LastPersistedBlockNumber.Should().Be(persistedBlockNumber);

            // Write a bit more
            for (int i = 10; i < 12; i++)
            {
                using (fullTrieStore.BeginBlockCommit(i))
                {
                    WriteRandomData(i);
                }
            }

            // Block writes
            writeBlocker.Reset();

            // Background pruning
            Task persistTask = Task.Run(() =>
            {
                testPruningStrategy.ShouldPruneEnabled = true;
                fullTrieStore.SyncPruneCheck();
                testPruningStrategy.ShouldPruneEnabled = false;
            });
            Thread.Sleep(100);

            // Bring block 5's node to block 12
            // This is done in commit buffer.
            using (fullTrieStore.BeginScope(Build.A.BlockHeader.WithStateRoot(ptree.RootHash).TestObject))
            {
                fullTrieStore.IsInCommitBufferMode.Should().BeTrue();
                using (fullTrieStore.BeginBlockCommit(12))
                {
                    WriteRandomData(5);
                    ptree.RootHash.Should().Be(persistedRootHash);
                }
            }

            writeBlocker.Set();

            await persistTask;

            // Write a bit more
            for (int i = 13; i < 13 + pruningBoundary; i++)
            {
                using (fullTrieStore.BeginBlockCommit(i))
                {
                    WriteRandomData(i);
                }
            }

            // Persisted nodes should be from block 12
            testPruningStrategy.ShouldPruneEnabled = true;
            fullTrieStore.SyncPruneCheck();
            testPruningStrategy.ShouldPruneEnabled = false;
            fullTrieStore.LastPersistedBlockNumber.Should().Be(12);

            fullTrieStore.PrunePersistedNodes();

            TrieStatsCollector collector = new TrieStatsCollector(new MemDb(), SimpleConsoleLogManager.Instance, expectAccounts: false);
            ptree.Accept(collector, rootHash: persistedRootHash);
            collector.Stats.MissingNodes.Should().Be(0);
        }
    }
}
