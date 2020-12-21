using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Trie.Test.Pruning
{
    [TestFixture]
    public class TreeStoreTests
    {
        private ILogManager _logManager = new OneLoggerLogManager(new NUnitLogger(LogLevel.Trace));


        [SetUp]
        public void Setup()
        {
        }

        [Test]
        public void Initial_memory_is_0()
        {
            TrieStore trieStore = new TrieStore(new MemDb(), new TestPruningStrategy(true), No.Persistence, _logManager);
            trieStore.MemoryUsedByDirtyCache.Should().Be(0);
        }
        
        [Test]
        public void Memory_with_one_node_is_288()
        {
            TrieNode trieNode = new TrieNode(NodeType.Leaf, Keccak.Zero); // 56B
        
            TrieStore trieStore = new TrieStore(new MemDb(), new TestPruningStrategy(true), No.Persistence, _logManager);
            trieStore.CommitNode(1234, new NodeCommitInfo(trieNode));
            trieStore.MemoryUsedByDirtyCache.Should().Be(
                trieNode.GetMemorySize(false));
        }



        [Test]
        public void Prunning_off_cache_should_not_change_commit_node()
        {
            TrieNode trieNode = new TrieNode(NodeType.Leaf, Keccak.Zero);
            TrieNode trieNode2 = new TrieNode(NodeType.Branch, TestItem.KeccakA);
            TrieNode trieNode3 = new TrieNode(NodeType.Branch, TestItem.KeccakB);

            TrieStore trieStore = new TrieStore(new MemDb(), No.Pruning, No.Persistence, _logManager);
            trieStore.CommitNode(1234, new NodeCommitInfo(trieNode));
            trieStore.FinishBlockCommit(TrieType.State, 1234, trieNode);
            trieStore.CommitNode(124, new NodeCommitInfo(trieNode2));
            trieStore.CommitNode(11234, new NodeCommitInfo(trieNode3));
            trieStore.MemoryUsedByDirtyCache.Should().Be(0);
        }

        [Test]
        public void Prunning_off_cache_should_not_find_cached_or_unknown()
        {
            TrieStore trieStore = new TrieStore(new MemDb(), No.Pruning, No.Persistence, _logManager);
            var returnedNode = trieStore.FindCachedOrUnknown(TestItem.KeccakA, true);
            var returnedNode2 = trieStore.FindCachedOrUnknown(TestItem.KeccakB, true);
            var returnedNode3 = trieStore.FindCachedOrUnknown(TestItem.KeccakC, true);
            Assert.AreEqual(NodeType.Unknown, returnedNode.NodeType);
            Assert.AreEqual(NodeType.Unknown, returnedNode2.NodeType);
            Assert.AreEqual(NodeType.Unknown, returnedNode3.NodeType);
            trieStore.MemoryUsedByDirtyCache.Should().Be(0);
        }

        [Test]
        public void FindCachedOrUnknown_CorrectlyCalculatedMemoryUsedByDirtyCache()
        {
            TrieStore trieStore = new TrieStore(new MemDb(), new TestPruningStrategy(true), No.Persistence, _logManager);
            var startSize = trieStore.MemoryUsedByDirtyCache;
            trieStore.FindCachedOrUnknown(TestItem.KeccakA);
            TrieNode trieNode = new TrieNode(NodeType.Leaf, Keccak.Zero);
            var oneKeccakSize = trieNode.GetMemorySize(false);
            Assert.AreEqual(startSize + oneKeccakSize, trieStore.MemoryUsedByDirtyCache);
            trieStore.FindCachedOrUnknown(TestItem.KeccakB);
            Assert.AreEqual(2 * oneKeccakSize + startSize, trieStore.MemoryUsedByDirtyCache);
            trieStore.FindCachedOrUnknown(TestItem.KeccakB);
            Assert.AreEqual(2 * oneKeccakSize + startSize, trieStore.MemoryUsedByDirtyCache);
            trieStore.FindCachedOrUnknown(TestItem.KeccakC);
            Assert.AreEqual(3 * oneKeccakSize + startSize, trieStore.MemoryUsedByDirtyCache);
            trieStore.FindCachedOrUnknown(TestItem.KeccakD, false);
            Assert.AreEqual(3 * oneKeccakSize + startSize, trieStore.MemoryUsedByDirtyCache);
        }

        [Test]
        public void Memory_with_two_nodes_is_correct()
        {
            TrieNode trieNode1 = new TrieNode(NodeType.Leaf, TestItem.KeccakA);
            TrieNode trieNode2 = new TrieNode(NodeType.Leaf, TestItem.KeccakB);
        
            TrieStore trieStore = new TrieStore(new MemDb(), new TestPruningStrategy(true), No.Persistence, _logManager);
            trieStore.CommitNode(1234, new NodeCommitInfo(trieNode1));
            trieStore.CommitNode(1234, new NodeCommitInfo(trieNode2));
            trieStore.MemoryUsedByDirtyCache.Should().Be(
                trieNode1.GetMemorySize(false) +
                trieNode2.GetMemorySize(false));
        }
        
        [Test]
        public void Memory_with_two_times_two_nodes_is_correct()
        {
            TrieNode trieNode1 = new TrieNode(NodeType.Leaf, TestItem.KeccakA);
            TrieNode trieNode2 = new TrieNode(NodeType.Leaf, TestItem.KeccakB);
            TrieNode trieNode3 = new TrieNode(NodeType.Leaf, TestItem.KeccakA);
            TrieNode trieNode4 = new TrieNode(NodeType.Leaf, TestItem.KeccakB);
        
            TrieStore trieStore = new TrieStore(new MemDb(), new TestPruningStrategy(true), No.Persistence, _logManager);
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
            TrieNode trieNode1 = new TrieNode(NodeType.Leaf, new byte[0]);
            trieNode1.ResolveKey(null!, true);
            TrieNode trieNode2 = new TrieNode(NodeType.Leaf, new byte[1]);
            trieNode2.ResolveKey(null!, true);
        
            TrieNode trieNode3 = new TrieNode(NodeType.Leaf, new byte[2]);
            trieNode3.ResolveKey(null!, true);
        
            TrieNode trieNode4 = new TrieNode(NodeType.Leaf, new byte[3]);
            trieNode4.ResolveKey(null!, true);
        
            TrieStore trieStore = new TrieStore(new MemDb(), new MemoryLimit(640), No.Persistence, _logManager);
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
            TrieNode trieNode1 = new TrieNode(NodeType.Leaf, new byte[0]);
            trieNode1.ResolveKey(null!, true);
            TrieNode trieNode2 = new TrieNode(NodeType.Leaf, new byte[1]);
            trieNode2.ResolveKey(null!, true);
        
            TrieNode trieNode3 = new TrieNode(NodeType.Leaf, new byte[2]);
            trieNode3.ResolveKey(null!, true);
        
            TrieNode trieNode4 = new TrieNode(NodeType.Leaf, new byte[3]);
            trieNode4.ResolveKey(null!, true);
        
            TrieStore trieStore = new TrieStore(new MemDb(), new MemoryLimit(512), No.Persistence, _logManager);
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

            TrieStore trieStore = new TrieStore(new MemDb(), new MemoryLimit(512), No.Persistence, _logManager);
            for (int i = 0; i < 1024; i++)
            {
                for (int j = 0; j < 1 + i % 3; j++)
                {
                    TrieNode trieNode = new TrieNode(NodeType.Leaf, new byte[0]); // 192B
                    trieNode.ResolveKey(NullTrieNodeResolver.Instance, true);
                    trieStore.CommitNode(i, new NodeCommitInfo(trieNode));
                }
        
                TrieNode fakeRoot = new TrieNode(NodeType.Leaf, new byte[0]); // 192B
                fakeRoot.ResolveKey(NullTrieNodeResolver.Instance, true);
                trieStore.FinishBlockCommit(TrieType.State, i, fakeRoot);
            }
        
            trieStore.MemoryUsedByDirtyCache.Should().BeLessThan(512 * 2);
        }

        [Test]
        public void Dispatcher_will_save_to_db_everything_from_snapshot_blocks()
        {
            TrieNode a = new TrieNode(NodeType.Leaf, new byte[0]); // 192B
            a.ResolveKey(NullTrieNodeResolver.Instance, true);

            MemDb memDb = new MemDb();

            TrieStore trieStore = new TrieStore(memDb, new MemoryLimit(16.MB()), new ConstantInterval(4), _logManager);

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
            TrieNode a = new TrieNode(NodeType.Leaf, new byte[0]); // 192B
            a.ResolveKey(NullTrieNodeResolver.Instance, true);

            MemDb memDb = new MemDb();

            TrieStore trieStore = new TrieStore(memDb, new MemoryLimit(16.MB()), No.Persistence, _logManager);

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
            MemDb memDb = new MemDb();
            memDb[Keccak.Zero.Bytes] = new byte[] {1, 2, 3};

            TrieStore trieStore = new TrieStore(memDb, _logManager);
            trieStore.LoadRlp(Keccak.Zero, false).Should().NotBeNull();
        }

        [Test]
        public void Will_get_persisted_on_snapshot_if_referenced()
        {
            TrieNode a = new TrieNode(NodeType.Leaf, new byte[0]); // 192B
            a.ResolveKey(NullTrieNodeResolver.Instance, true);

            MemDb memDb = new MemDb();

            TrieStore trieStore = new TrieStore(memDb, new MemoryLimit(16.MB()), new ConstantInterval(4), _logManager);

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
            TrieNode a = new TrieNode(NodeType.Leaf, new byte[0]);
            a.ResolveKey(NullTrieNodeResolver.Instance, true);

            TrieNode b = new TrieNode(NodeType.Leaf, new byte[1]);
            b.ResolveKey(NullTrieNodeResolver.Instance, true);

            MemDb memDb = new MemDb();

            TrieStore trieStore = new TrieStore(memDb, new MemoryLimit(16.MB()), new ConstantInterval(4), _logManager);

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
            TrieNode a = new TrieNode(NodeType.Leaf, new byte[] {1});
            a.ResolveKey(NullTrieNodeResolver.Instance, true);

            TrieNode b = new TrieNode(NodeType.Leaf, new byte[] {2});
            b.ResolveKey(NullTrieNodeResolver.Instance, true);

            MemDb memDb = new MemDb();

            TrieStore trieStore = new TrieStore(memDb, new MemoryLimit(16.MB()), new ConstantInterval(4), _logManager);

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

        private AccountDecoder _accountDecoder = new AccountDecoder();

        [Test]
        public void Will_store_storage_on_snapshot()
        {
            TrieNode storage1 = new TrieNode(NodeType.Leaf, new byte[2]);
            storage1.ResolveKey(NullTrieNodeResolver.Instance, true);

            TrieNode a = new TrieNode(NodeType.Leaf);
            Account account = new Account(1, 1, storage1.Keccak, Keccak.OfAnEmptyString);
            a.Value = _accountDecoder.Encode(account).Bytes;
            a.Key = HexPrefix.Leaf("abc");
            a.ResolveKey(NullTrieNodeResolver.Instance, true);

            MemDb memDb = new MemDb();

            TrieStore trieStore = new TrieStore(memDb, new MemoryLimit(16.MB()), new ConstantInterval(4), _logManager);
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
            TrieNode storage1 = new TrieNode(NodeType.Leaf, new byte[2]);
            storage1.ResolveKey(NullTrieNodeResolver.Instance, true);

            TrieNode a = new TrieNode(NodeType.Leaf);
            Account account = new Account(1, 1, storage1.Keccak, Keccak.OfAnEmptyString);
            a.Value = _accountDecoder.Encode(account).Bytes;
            a.Key = HexPrefix.Leaf("abc");
            a.ResolveKey(NullTrieNodeResolver.Instance, true);

            TrieNode b = new TrieNode(NodeType.Leaf, new byte[1]);
            b.ResolveKey(NullTrieNodeResolver.Instance, true);

            MemDb memDb = new MemDb();

            TrieStore trieStore = new TrieStore(memDb, new MemoryLimit(16.MB()), new ConstantInterval(4), _logManager);

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
            TrieNode storage1 = new TrieNode(NodeType.Leaf, new byte[32]);
            storage1.ResolveKey(NullTrieNodeResolver.Instance, true);

            TrieNode a = new TrieNode(NodeType.Leaf);
            Account account = new Account(1, 1, storage1.Keccak, Keccak.OfAnEmptyString);
            a.Value = _accountDecoder.Encode(account).Bytes;
            a.Key = HexPrefix.Leaf("abc");
            a.ResolveKey(NullTrieNodeResolver.Instance, true);

            TrieNode storage2 = new TrieNode(NodeType.Leaf, new byte[32]);
            storage2.ResolveKey(NullTrieNodeResolver.Instance, true);

            TrieNode b = new TrieNode(NodeType.Leaf);
            Account accountB = new Account(2, 1, storage2.Keccak, Keccak.OfAnEmptyString);
            b.Value = _accountDecoder.Encode(accountB).Bytes;
            b.Key = HexPrefix.Leaf("abcd");
            b.ResolveKey(NullTrieNodeResolver.Instance, true);

            TrieNode branch = new TrieNode(NodeType.Branch);
            branch.SetChild(0, a);
            branch.SetChild(1, b);
            branch.ResolveKey(NullTrieStore.Instance, true);

            MemDb memDb = new MemDb();

            TrieStore trieStore = new TrieStore(memDb, new MemoryLimit(16.MB()), new ConstantInterval(4), _logManager);

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
    }
}
