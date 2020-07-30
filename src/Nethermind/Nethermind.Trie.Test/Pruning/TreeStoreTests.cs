using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Trie.Test.Pruning
{
    [TestFixture]
    public class TreeStoreTests
    {
        private ITrieNodeCache _trieNodeCache;
        private IRefsJournal _refsJournal;

        [SetUp]
        public void Setup()
        {
            _trieNodeCache = new TrieNodeCache(LimboLogs.Instance);
            _refsJournal = new RefsJournal(_trieNodeCache, LimboLogs.Instance);
        }

        [Test]
        public void Initial_memory_is_96()
        {
            TreeStore treeStore = new TreeStore(_trieNodeCache, new MemDb(), _refsJournal, LimboLogs.Instance, 1.MB());
            treeStore.MemorySize.Should().Be(96);
        }

        [Test]
        public void Memory_with_one_node_is_288()
        {
            TrieNode trieNode = new TrieNode(NodeType.Unknown, Keccak.Zero); // 56B

            TreeStore treeStore = new TreeStore(_trieNodeCache, new MemDb(), _refsJournal, LimboLogs.Instance, 1.MB());
            treeStore.Commit(1234, new NodeCommitInfo(trieNode));
            treeStore.MemorySize.Should().Be(
                96 /* committer */ +
                88 /* block package */ +
                48 /* linked list node size */ +
                trieNode.GetMemorySize(false));
        }

        [Test]
        public void Memory_with_two_nodes_is_correct()
        {
            TrieNode trieNode1 = new TrieNode(NodeType.Unknown, TestItem.KeccakA);
            TrieNode trieNode2 = new TrieNode(NodeType.Unknown, TestItem.KeccakB);

            TreeStore treeStore = new TreeStore(_trieNodeCache, new MemDb(), _refsJournal, LimboLogs.Instance, 1.MB());
            treeStore.Commit(1234, new NodeCommitInfo(trieNode1));
            treeStore.Commit(1234, new NodeCommitInfo(trieNode2));
            treeStore.MemorySize.Should().Be(
                96 /* committer */ +
                88 /* block package */ +
                48 /* linked list node size */ +
                trieNode1.GetMemorySize(false) +
                trieNode2.GetMemorySize(false));
        }

        [Test]
        public void Memory_with_two_times_two_nodes_is_592()
        {
            TrieNode trieNode1 = new TrieNode(NodeType.Unknown, TestItem.KeccakA);
            TrieNode trieNode2 = new TrieNode(NodeType.Unknown, TestItem.KeccakB);

            TreeStore treeStore = new TreeStore(_trieNodeCache, new MemDb(), _refsJournal, LimboLogs.Instance, 1.MB());
            treeStore.Commit(1234, new NodeCommitInfo(trieNode1));
            treeStore.Commit(1234, new NodeCommitInfo(trieNode2));
            treeStore.FinalizeBlock(1234, trieNode2);
            treeStore.Commit(1235, new NodeCommitInfo(trieNode1));
            treeStore.Commit(1235, new NodeCommitInfo(trieNode2));
            treeStore.MemorySize.Should().Be(
                96 /* committer */ +
                2 * 88 /* block package */ +
                2 * 48 /* linked list node size */ +
                trieNode1.GetMemorySize(false) +
                trieNode2.GetMemorySize(false));
        }

        [Test]
        public void Dispatcher_will_try_to_clear_memory()
        {
            TrieNode trieNode1 = new TrieNode(NodeType.Leaf, new byte[0]);
            trieNode1.ResolveKey(null!, true);
            trieNode1.Refs = 1;
            TrieNode trieNode2 = new TrieNode(NodeType.Leaf, new byte[1]);
            trieNode2.ResolveKey(null!, true);
            trieNode2.Refs = 1;

            TrieNode trieNode3 = new TrieNode(NodeType.Leaf, new byte[2]);
            trieNode3.ResolveKey(null!, true);
            trieNode3.Refs = 1;

            TrieNode trieNode4 = new TrieNode(NodeType.Leaf, new byte[3]);
            trieNode4.ResolveKey(null!, true);
            trieNode4.Refs = 1;

            TreeStore treeStore = new TreeStore(_trieNodeCache, new MemDb(), _refsJournal, LimboLogs.Instance, 640);
            treeStore.Commit(1234, new NodeCommitInfo(trieNode1));
            treeStore.Commit(1234, new NodeCommitInfo(trieNode2));
            treeStore.FinalizeBlock(1234, trieNode2);
            treeStore.Commit(1235, new NodeCommitInfo(trieNode3));
            treeStore.Commit(1235, new NodeCommitInfo(trieNode4));
            treeStore.MemorySize.Should().Be(
                96 /* committer */ +
                1 * 88 /* block package */ +
                1 * 48 /* linked list node size */ +
                trieNode3.GetMemorySize(false) +
                trieNode4.GetMemorySize(false));
        }

        [Test]
        public void Dispatcher_will_try_to_clear_memory_the_soonest_possible()
        {
            TrieNode trieNode1 = new TrieNode(NodeType.Leaf, new byte[0]);
            trieNode1.ResolveKey(null!, true);
            trieNode1.Refs = 1;
            TrieNode trieNode2 = new TrieNode(NodeType.Leaf, new byte[1]);
            trieNode2.ResolveKey(null!, true);
            trieNode2.Refs = 1;

            TrieNode trieNode3 = new TrieNode(NodeType.Leaf, new byte[2]);
            trieNode3.ResolveKey(null!, true);
            trieNode3.Refs = 1;

            TrieNode trieNode4 = new TrieNode(NodeType.Leaf, new byte[3]);
            trieNode4.ResolveKey(null!, true);
            trieNode4.Refs = 1;

            TreeStore treeStore = new TreeStore(_trieNodeCache, new MemDb(), _refsJournal, LimboLogs.Instance, 512);
            treeStore.Commit(1234, new NodeCommitInfo(trieNode1));
            treeStore.Commit(1234, new NodeCommitInfo(trieNode2));
            treeStore.FinalizeBlock(1234, trieNode2);
            treeStore.Commit(1235, new NodeCommitInfo(trieNode3));
            treeStore.Commit(1235, new NodeCommitInfo(trieNode4));
            treeStore.MemorySize.Should().Be(
                96 /* committer */ +
                1 * 88 /* block package */ +
                1 * 48 /* linked list node size */ +
                trieNode3.GetMemorySize(false) +
                trieNode4.GetMemorySize(false));
        }

        [Test]
        public void Dispatcher_will_always_try_to_clear_memory()
        {
            TrieNode trieNode = new TrieNode(NodeType.Leaf, new byte[0]); // 192B
            trieNode.ResolveKey(NullTrieNodeResolver.Instance, true);

            TreeStore treeStore = new TreeStore(_trieNodeCache, new MemDb(), _refsJournal, LimboLogs.Instance, 512);
            for (int i = 0; i < 1024; i++)
            {
                for (int j = 0; j < 1 + i % 3; j++)
                {
                    treeStore.Commit(i, new NodeCommitInfo(trieNode));
                }
                
                treeStore.FinalizeBlock(i, trieNode);
            }

            treeStore.MemorySize.Should().BeLessThan(512 * 2);
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void Dispatcher_will_save_to_db_everything_from_snapshot_blocks(int refCount)
        {
            TrieNode a = new TrieNode(NodeType.Leaf, new byte[0]); // 192B
            a.ResolveKey(NullTrieNodeResolver.Instance, true);

            MemDb memDb = new MemDb();
            
            TreeStore treeStore = new TreeStore(_trieNodeCache, memDb, _refsJournal, LimboLogs.Instance, 16.MB(), 4);

            a.Refs = refCount;
            treeStore.Commit(0, new NodeCommitInfo(a));
            treeStore.FinalizeBlock(0, a);
            treeStore.FinalizeBlock(1, a);
            treeStore.FinalizeBlock(2, a);
            treeStore.FinalizeBlock(3, a);
            treeStore.FinalizeBlock(4, a);

            memDb[a.Keccak!.Bytes].Should().NotBeNull();
            treeStore.IsInMemory(a.Keccak).Should().BeFalse();
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void Stays_in_memory_until_persisted(int refCount)
        {
            TrieNode a = new TrieNode(NodeType.Leaf, new byte[0]); // 192B
            a.ResolveKey(NullTrieNodeResolver.Instance, true);

            MemDb memDb = new MemDb();

            TreeStore treeStore = new TreeStore(_trieNodeCache, memDb, _refsJournal, LimboLogs.Instance, 16.MB(), 4);

            a.Refs = refCount;
            treeStore.Commit(0, new NodeCommitInfo(a));
            treeStore.FinalizeBlock(0, a);
            treeStore.FinalizeBlock(1, a);
            treeStore.FinalizeBlock(2, a);
            treeStore.FinalizeBlock(3, a);
            //  <- do not persist in this test

            memDb[a.Keccak!.Bytes].Should().BeNull();
            treeStore.IsInMemory(a.Keccak).Should().BeTrue();
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void Will_get_persisted_on_snapshot_if_referenced(int refCount)
        {
            TrieNode a = new TrieNode(NodeType.Leaf, new byte[0]); // 192B
            a.ResolveKey(NullTrieNodeResolver.Instance, true);

            MemDb memDb = new MemDb();

            TreeStore treeStore = new TreeStore(_trieNodeCache, memDb, _refsJournal, LimboLogs.Instance, 16.MB(), 4);

            a.Refs = refCount;
            treeStore.FinalizeBlock(0, null);
            treeStore.Commit(1, new NodeCommitInfo(a));
            treeStore.FinalizeBlock(1, a);
            treeStore.FinalizeBlock(2, a);
            treeStore.FinalizeBlock(3, a);
            treeStore.FinalizeBlock(4, a);
            treeStore.FinalizeBlock(5, a);
            treeStore.FinalizeBlock(6, a);
            treeStore.FinalizeBlock(7, a);
            treeStore.FinalizeBlock(8, a);

            memDb[a.Keccak!.Bytes].Should().NotBeNull();
            treeStore.IsInMemory(a.Keccak).Should().BeFalse();
        }

        [Test]
        public void Will_not_get_dropped_on_snapshot_if_unreferenced_in_later_blocks()
        {
            TrieNode a = new TrieNode(NodeType.Leaf, new byte[0]);
            a.ResolveKey(NullTrieNodeResolver.Instance, true);

            TrieNode b = new TrieNode(NodeType.Leaf, new byte[1]);
            b.ResolveKey(NullTrieNodeResolver.Instance, true);

            MemDb memDb = new MemDb();

            TreeStore treeStore = new TreeStore(_trieNodeCache, memDb, _refsJournal, LimboLogs.Instance, 16.MB(), 4);

            a.Refs = 1;
            treeStore.FinalizeBlock(0, null);
            treeStore.Commit(1, new NodeCommitInfo(a));
            treeStore.FinalizeBlock(1, a);
            treeStore.FinalizeBlock(2, a);
            treeStore.FinalizeBlock(3, a);
            treeStore.FinalizeBlock(4, a);
            treeStore.FinalizeBlock(5, a);
            treeStore.FinalizeBlock(6, a);
            // TODO: this is actually a bug since 'a' was referenced from root at the time of block 4
            a.Refs = 0;
            treeStore.Commit(7, new NodeCommitInfo(b));
            treeStore.FinalizeBlock(7, b);
            treeStore.FinalizeBlock(8, b);

            memDb[a.Keccak!.Bytes].Should().NotBeNull();
            treeStore.IsInMemory(a.Keccak).Should().BeFalse();
        }

        [Test]
        public void Will_get_dropped_on_snapshot_if_it_was_a_transient_node()
        {
            TrieNode a = new TrieNode(NodeType.Leaf, new byte[0]);
            a.ResolveKey(NullTrieNodeResolver.Instance, true);

            TrieNode b = new TrieNode(NodeType.Leaf, new byte[1]);
            b.ResolveKey(NullTrieNodeResolver.Instance, true);

            MemDb memDb = new MemDb();
            
            ITrieNodeCache cache = new TrieNodeCache(LimboLogs.Instance);
            TreeStore treeStore = new TreeStore(cache,  memDb, _refsJournal, LimboLogs.Instance, 16.MB(), 4);

            a.Refs = 1;
            treeStore.FinalizeBlock(0, null);
            treeStore.Commit(1, new NodeCommitInfo(a));
            treeStore.FinalizeBlock(1, a);
            treeStore.FinalizeBlock(2, a);
            a.Refs = 0;
            treeStore.Commit(3, new NodeCommitInfo(b)); // <- new root
            treeStore.FinalizeBlock(3, b);
            treeStore.FinalizeBlock(4, a);
            treeStore.FinalizeBlock(5, a);
            treeStore.FinalizeBlock(6, a);
            treeStore.FinalizeBlock(7, a);
            treeStore.FinalizeBlock(8, a);

            memDb[a.Keccak!.Bytes].Should().BeNull();
            treeStore.IsInMemory(a.Keccak).Should().BeFalse();
        }
    }
}