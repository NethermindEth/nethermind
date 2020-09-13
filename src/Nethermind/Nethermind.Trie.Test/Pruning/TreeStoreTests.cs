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
        private ITrieNodeCache _trieNodeCache;

        [SetUp]
        public void Setup()
        {
            _trieNodeCache = new TrieNodeCache(_logManager);
        }

        // [Test]
        // public void Initial_memory_is_96()
        // {
        //     TrieStore trieStore = new TrieStore(_trieNodeCache, new MemDb(), No.Pruning, No.Persistence, _logManager);
        //     trieStore.MemorySize.Should().Be(96);
        // }
        //
        // [Test]
        // public void Memory_with_one_node_is_288()
        // {
        //     TrieNode trieNode = new TrieNode(NodeType.Leaf, Keccak.Zero); // 56B
        //
        //     TrieStore trieStore = new TrieStore(_trieNodeCache, new MemDb(), No.Pruning, No.Persistence, _logManager);
        //     trieStore.CommitOneNode(1234, new NodeCommitInfo(trieNode));
        //     trieStore.MemorySize.Should().Be(
        //         96 /* committer */ +
        //         88 /* block package */ +
        //         48 /* linked list node size */ +
        //         trieNode.GetMemorySize(false));
        // }
        //
        // [Test]
        // public void Memory_with_two_nodes_is_correct()
        // {
        //     TrieNode trieNode1 = new TrieNode(NodeType.Leaf, TestItem.KeccakA);
        //     TrieNode trieNode2 = new TrieNode(NodeType.Leaf, TestItem.KeccakB);
        //
        //     TrieStore trieStore = new TrieStore(_trieNodeCache, new MemDb(), No.Pruning, No.Persistence, _logManager);
        //     trieStore.CommitOneNode(1234, new NodeCommitInfo(trieNode1));
        //     trieStore.CommitOneNode(1234, new NodeCommitInfo(trieNode2));
        //     trieStore.MemorySize.Should().Be(
        //         96 /* committer */ +
        //         88 /* block package */ +
        //         48 /* linked list node size */ +
        //         trieNode1.GetMemorySize(false) +
        //         trieNode2.GetMemorySize(false));
        // }
        //
        // [Test]
        // public void Memory_with_two_times_two_nodes_is_correct()
        // {
        //     TrieNode trieNode1 = new TrieNode(NodeType.Leaf, TestItem.KeccakA);
        //     TrieNode trieNode2 = new TrieNode(NodeType.Leaf, TestItem.KeccakB);
        //
        //     TrieStore trieStore = new TrieStore(_trieNodeCache, new MemDb(), No.Pruning, No.Persistence, _logManager);
        //     trieStore.CommitOneNode(1234, new NodeCommitInfo(trieNode1));
        //     trieStore.CommitOneNode(1234, new NodeCommitInfo(trieNode2));
        //     trieStore.FinishBlockCommit(TrieType.State, 1234, trieNode2);
        //     trieStore.CommitOneNode(1235, new NodeCommitInfo(trieNode1));
        //     trieStore.CommitOneNode(1235, new NodeCommitInfo(trieNode2));
        //     
        //     // depending on whether the node gets resolved it gives different values here in debugging and run
        //     // needs some attention
        //     trieStore.MemorySize.Should().BeLessThan(
        //         96 /* committer */ +
        //         2 * 88 /* block package */ +
        //         2 * 48 /* linked list node size */ +
        //         trieNode1.GetMemorySize(false) +
        //         trieNode2.GetMemorySize(false));
        // }

        // [Test]
        // public void Dispatcher_will_try_to_clear_memory()
        // {
        //     TrieNode trieNode1 = new TrieNode(NodeType.Leaf, new byte[0]);
        //     trieNode1.ResolveKey(null!, true);
        //     TrieNode trieNode2 = new TrieNode(NodeType.Leaf, new byte[1]);
        //     trieNode2.ResolveKey(null!, true);
        //
        //     TrieNode trieNode3 = new TrieNode(NodeType.Leaf, new byte[2]);
        //     trieNode3.ResolveKey(null!, true);
        //
        //     TrieNode trieNode4 = new TrieNode(NodeType.Leaf, new byte[3]);
        //     trieNode4.ResolveKey(null!, true);
        //
        //     TrieStore trieStore = new TrieStore(_trieNodeCache, new MemDb(), new MemoryLimit(640), No.Persistence, _logManager);
        //     trieStore.CommitOneNode(1234, new NodeCommitInfo(trieNode1));
        //     trieStore.CommitOneNode(1234, new NodeCommitInfo(trieNode2));
        //     trieStore.FinishBlockCommit(TrieType.State, 1234, trieNode2);
        //     trieStore.CommitOneNode(1235, new NodeCommitInfo(trieNode3));
        //     trieStore.CommitOneNode(1235, new NodeCommitInfo(trieNode4));
        //     trieStore.FinishBlockCommit(TrieType.State, 1235, trieNode2);
        //     trieStore.FinishBlockCommit(TrieType.State, 1236, trieNode2);
        //     trieStore.MemorySize.Should().Be(
        //         96 /* committer */ +
        //         1 * 88 /* block package */ +
        //         1 * 48 /* linked list node size */ +
        //         trieNode3.GetMemorySize(false) +
        //         trieNode4.GetMemorySize(false) +
        //         1 * 88 /* block package */ +
        //         1 * 48 /* linked list node size */);
        // }

        // [Test]
        // public void Dispatcher_will_try_to_clear_memory_the_soonest_possible()
        // {
        //     TrieNode trieNode1 = new TrieNode(NodeType.Leaf, new byte[0]);
        //     trieNode1.ResolveKey(null!, true);
        //     TrieNode trieNode2 = new TrieNode(NodeType.Leaf, new byte[1]);
        //     trieNode2.ResolveKey(null!, true);
        //
        //     TrieNode trieNode3 = new TrieNode(NodeType.Leaf, new byte[2]);
        //     trieNode3.ResolveKey(null!, true);
        //
        //     TrieNode trieNode4 = new TrieNode(NodeType.Leaf, new byte[3]);
        //     trieNode4.ResolveKey(null!, true);
        //
        //     TrieStore trieStore = new TrieStore(_trieNodeCache, new MemDb(), new MemoryLimit(512), No.Persistence, _logManager);
        //     trieStore.CommitOneNode(1234, new NodeCommitInfo(trieNode1));
        //     trieStore.CommitOneNode(1234, new NodeCommitInfo(trieNode2));
        //     trieStore.FinishBlockCommit(TrieType.State, 1234, trieNode2);
        //     trieStore.CommitOneNode(1235, new NodeCommitInfo(trieNode3));
        //     trieStore.CommitOneNode(1235, new NodeCommitInfo(trieNode4));
        //     trieStore.MemorySize.Should().Be(
        //         96 /* committer */ +
        //         1 * 88 /* block package */ +
        //         1 * 48 /* linked list node size */ +
        //         trieNode3.GetMemorySize(false) +
        //         trieNode4.GetMemorySize(false));
        // }

        // [Test]
        // public void Dispatcher_will_always_try_to_clear_memory()
        // {
        //     TrieNode trieNode = new TrieNode(NodeType.Leaf, new byte[0]); // 192B
        //     trieNode.ResolveKey(NullTrieNodeResolver.Instance, true);
        //
        //     TrieStore trieStore = new TrieStore(_trieNodeCache, new MemDb(), new MemoryLimit(512), No.Persistence, _logManager);
        //     for (int i = 0; i < 1024; i++)
        //     {
        //         for (int j = 0; j < 1 + i % 3; j++)
        //         {
        //             trieStore.CommitOneNode(i, new NodeCommitInfo(trieNode));
        //         }
        //
        //         trieStore.FinishBlockCommit(TrieType.State, i, trieNode);
        //     }
        //
        //     trieStore.MemorySize.Should().BeLessThan(512 * 2);
        // }

        [Test]
        public void Dispatcher_will_save_to_db_everything_from_snapshot_blocks()
        {
            TrieNode a = new TrieNode(NodeType.Leaf, new byte[0]); // 192B
            a.ResolveKey(NullTrieNodeResolver.Instance, true);

            MemDb memDb = new MemDb();

            TrieStore trieStore = new TrieStore(_trieNodeCache, memDb, new DepthAndMemoryBased(4, 16.MB()), new ConstantInterval(4), _logManager);

            trieStore.CommitOneNode(0, new NodeCommitInfo(a));
            trieStore.FinishBlockCommit(TrieType.State, 0, a);
            trieStore.FinishBlockCommit(TrieType.State, 1, a);
            trieStore.FinishBlockCommit(TrieType.State, 2, a);
            trieStore.FinishBlockCommit(TrieType.State, 3, a);
            trieStore.FinishBlockCommit(TrieType.State, 4, a);

            memDb[a.Keccak!.Bytes].Should().NotBeNull();
            trieStore.IsInMemory(a.Keccak).Should().BeFalse();
        }

        [Test]
        public void Stays_in_memory_until_persisted()
        {
            TrieNode a = new TrieNode(NodeType.Leaf, new byte[0]); // 192B
            a.ResolveKey(NullTrieNodeResolver.Instance, true);

            MemDb memDb = new MemDb();

            TrieStore trieStore = new TrieStore(_trieNodeCache, memDb, new DepthAndMemoryBased(4, 16.MB()), No.Persistence, _logManager);

            trieStore.CommitOneNode(0, new NodeCommitInfo(a));
            trieStore.FinishBlockCommit(TrieType.State, 0, a);
            trieStore.FinishBlockCommit(TrieType.State, 1, a);
            trieStore.FinishBlockCommit(TrieType.State, 2, a);
            trieStore.FinishBlockCommit(TrieType.State, 3, a);
            //  <- do not persist in this test

            memDb[a.Keccak!.Bytes].Should().BeNull();
            trieStore.IsInMemory(a.Keccak).Should().BeTrue();
        }

        [Test]
        public void Will_get_persisted_on_snapshot_if_referenced()
        {
            TrieNode a = new TrieNode(NodeType.Leaf, new byte[0]); // 192B
            a.ResolveKey(NullTrieNodeResolver.Instance, true);

            MemDb memDb = new MemDb();

            TrieStore trieStore = new TrieStore(_trieNodeCache, memDb, new DepthAndMemoryBased(4, 16.MB()), new ConstantInterval(4), _logManager);

            trieStore.FinishBlockCommit(TrieType.State, 0, null);
            trieStore.CommitOneNode(1, new NodeCommitInfo(a));
            trieStore.FinishBlockCommit(TrieType.State, 1, a);
            trieStore.FinishBlockCommit(TrieType.State, 2, a);
            trieStore.FinishBlockCommit(TrieType.State, 3, a);
            trieStore.FinishBlockCommit(TrieType.State, 4, a);
            trieStore.FinishBlockCommit(TrieType.State, 5, a);
            trieStore.FinishBlockCommit(TrieType.State, 6, a);
            trieStore.FinishBlockCommit(TrieType.State, 7, a);
            trieStore.FinishBlockCommit(TrieType.State, 8, a);

            memDb[a.Keccak!.Bytes].Should().NotBeNull();
            trieStore.IsInMemory(a.Keccak).Should().BeFalse();
        }

        [Test]
        public void Will_not_get_dropped_on_snapshot_if_unreferenced_in_later_blocks()
        {
            TrieNode a = new TrieNode(NodeType.Leaf, new byte[0]);
            a.ResolveKey(NullTrieNodeResolver.Instance, true);

            TrieNode b = new TrieNode(NodeType.Leaf, new byte[1]);
            b.ResolveKey(NullTrieNodeResolver.Instance, true);

            MemDb memDb = new MemDb();

            TrieStore trieStore = new TrieStore(_trieNodeCache, memDb, new DepthAndMemoryBased(4, 16.MB()), new ConstantInterval(4), _logManager);

            trieStore.FinishBlockCommit(TrieType.State, 0, null);
            trieStore.CommitOneNode(1, new NodeCommitInfo(a));
            trieStore.FinishBlockCommit(TrieType.State, 1, a);
            trieStore.FinishBlockCommit(TrieType.State, 2, a);
            trieStore.FinishBlockCommit(TrieType.State, 3, a);
            trieStore.FinishBlockCommit(TrieType.State, 4, a);
            trieStore.FinishBlockCommit(TrieType.State, 5, a);
            trieStore.FinishBlockCommit(TrieType.State, 6, a);
            trieStore.CommitOneNode(7, new NodeCommitInfo(b));
            trieStore.FinishBlockCommit(TrieType.State, 7, b);
            trieStore.FinishBlockCommit(TrieType.State, 8, b);

            memDb[a.Keccak!.Bytes].Should().NotBeNull();
            trieStore.IsInMemory(a.Keccak).Should().BeFalse();
        }

        [Test]
        public void Will_get_dropped_on_snapshot_if_it_was_a_transient_node()
        {
            TrieNode a = new TrieNode(NodeType.Leaf, new byte[0]);
            a.ResolveKey(NullTrieNodeResolver.Instance, true);

            TrieNode b = new TrieNode(NodeType.Leaf, new byte[1]);
            b.ResolveKey(NullTrieNodeResolver.Instance, true);

            MemDb memDb = new MemDb();

            ITrieNodeCache cache = new TrieNodeCache(_logManager);
            TrieStore trieStore = new TrieStore(cache, memDb, new DepthAndMemoryBased(4, 16.MB()), No.Persistence, _logManager);

            trieStore.FinishBlockCommit(TrieType.State, 0, null);
            trieStore.CommitOneNode(1, new NodeCommitInfo(a));
            trieStore.FinishBlockCommit(TrieType.State, 1, a);
            trieStore.FinishBlockCommit(TrieType.State, 2, a);
            trieStore.CommitOneNode(3, new NodeCommitInfo(b)); // <- new root
            trieStore.FinishBlockCommit(TrieType.State, 3, b);
            trieStore.FinishBlockCommit(TrieType.State, 4, b); // should be 'a' to test properly
            trieStore.FinishBlockCommit(TrieType.State, 5, b); // should be 'a' to test properly
            trieStore.FinishBlockCommit(TrieType.State, 6, b); // should be 'a' to test properly
            trieStore.FinishBlockCommit(TrieType.State, 7, b); // should be 'a' to test properly
            trieStore.FinishBlockCommit(TrieType.State, 8, b); // should be 'a' to test properly

            memDb[a.Keccak!.Bytes].Should().BeNull();
            trieStore.IsInMemory(a.Keccak).Should().BeFalse();
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

            ITrieNodeCache cache = new TrieNodeCache(_logManager);
            TrieStore trieStore = new TrieStore(cache, memDb, new DepthAndMemoryBased(4, 16.MB()), new ConstantInterval(4), _logManager);
            trieStore.FinishBlockCommit(TrieType.State, 0, null);
            trieStore.CommitOneNode(1, new NodeCommitInfo(a));
            trieStore.CommitOneNode(1, new NodeCommitInfo(storage1));
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
            trieStore.IsInMemory(a.Keccak).Should().BeFalse();
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

            ITrieNodeCache cache = new TrieNodeCache(_logManager);
            TrieStore trieStore = new TrieStore(cache, memDb, new DepthAndMemoryBased(4, 16.MB()), No.Persistence, _logManager);

            trieStore.FinishBlockCommit(TrieType.State, 0, null);
            trieStore.CommitOneNode(1, new NodeCommitInfo(a));
            trieStore.CommitOneNode(1, new NodeCommitInfo(storage1));
            trieStore.FinishBlockCommit(TrieType.Storage, 1, storage1);
            trieStore.FinishBlockCommit(TrieType.State, 1, a);
            trieStore.FinishBlockCommit(TrieType.State, 2, a);
            trieStore.CommitOneNode(3, new NodeCommitInfo(b)); // <- new root
            trieStore.FinishBlockCommit(TrieType.State, 3, b);
            trieStore.FinishBlockCommit(TrieType.State, 4, b); // should be 'a' to test properly
            trieStore.FinishBlockCommit(TrieType.State, 5, b); // should be 'a' to test properly
            trieStore.FinishBlockCommit(TrieType.State, 6, b); // should be 'a' to test properly
            trieStore.FinishBlockCommit(TrieType.State, 7, b); // should be 'a' to test properly
            trieStore.FinishBlockCommit(TrieType.State, 8, b); // should be 'a' to test properly

            memDb[a.Keccak!.Bytes].Should().BeNull();
            memDb[storage1.Keccak!.Bytes].Should().BeNull();
            trieStore.IsInMemory(a.Keccak).Should().BeFalse();
            trieStore.IsInMemory(storage1.Keccak).Should().BeFalse();
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

            ITrieNodeCache cache = new TrieNodeCache(_logManager);
            TrieStore trieStore = new TrieStore(cache, memDb, new DepthAndMemoryBased(4, 16.MB()),new ConstantInterval(4), _logManager);

            trieStore.FinishBlockCommit(TrieType.State, 0, null);
            trieStore.CommitOneNode(1, new NodeCommitInfo(storage1));
            trieStore.FinishBlockCommit(TrieType.Storage, 1, storage1);
            trieStore.CommitOneNode(1, new NodeCommitInfo(storage2));
            trieStore.FinishBlockCommit(TrieType.Storage, 1, storage2);
            trieStore.CommitOneNode(1, new NodeCommitInfo(a));
            trieStore.CommitOneNode(1, new NodeCommitInfo(b));
            trieStore.CommitOneNode(1, new NodeCommitInfo(branch));
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
            trieStore.IsInMemory(a.Keccak).Should().BeFalse();
            trieStore.IsInMemory(storage1.Keccak).Should().BeTrue();
        }
    }
}