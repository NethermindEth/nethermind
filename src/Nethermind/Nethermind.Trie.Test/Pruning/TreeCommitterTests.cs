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
    public class TreeCommitterTests
    {
        [Test]
        public void Initial_memory_is_96()
        {
            TreeCommitter treeCommitter = new TreeCommitter(new MemDb(), LimboLogs.Instance, 1.MB());
            treeCommitter.MemorySize.Should().Be(96);
        }
        
        [Test]
        public void Memory_with_one_node_is_288()
        {
            TrieNode trieNode = new TrieNode(NodeType.Unknown, Keccak.Zero); // 56B
            
            TreeCommitter treeCommitter = new TreeCommitter(new MemDb(), LimboLogs.Instance, 1.MB());
            treeCommitter.Commit(1234, trieNode);
            treeCommitter.MemorySize.Should().Be(
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
            
            TreeCommitter treeCommitter = new TreeCommitter(new MemDb(), LimboLogs.Instance, 1.MB());
            treeCommitter.Commit(1234, trieNode1);
            treeCommitter.Commit(1234, trieNode2);
            treeCommitter.MemorySize.Should().Be(
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
            
            TreeCommitter treeCommitter = new TreeCommitter(new MemDb(), LimboLogs.Instance, 1.MB());
            treeCommitter.Commit(1234, trieNode1);
            treeCommitter.Commit(1234, trieNode2);
            treeCommitter.Commit(1235, trieNode1);
            treeCommitter.Commit(1235, trieNode2);
            treeCommitter.MemorySize.Should().Be(
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

            TreeCommitter treeCommitter = new TreeCommitter(new MemDb(), LimboLogs.Instance, 640);
            treeCommitter.Commit(1234, trieNode1);
            treeCommitter.Commit(1234, trieNode2);
            treeCommitter.Commit(1235, trieNode3);
            treeCommitter.Commit(1235, trieNode4);
            treeCommitter.MemorySize.Should().Be(
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

            TreeCommitter treeCommitter = new TreeCommitter(new MemDb(), LimboLogs.Instance, 512);
            treeCommitter.Commit(1234, trieNode1);
            treeCommitter.Commit(1234, trieNode2);
            treeCommitter.Commit(1235, trieNode3);
            treeCommitter.Commit(1235, trieNode4);
            treeCommitter.MemorySize.Should().Be(
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
            trieNode.ResolveKey(null, true);

            TreeCommitter treeCommitter = new TreeCommitter(new MemDb(), LimboLogs.Instance, 512);
            for (int i = 0; i < 1024; i++)
            {
                for (int j = 0; j < 1 + i % 3; j++)
                {
                    treeCommitter.Commit(i, trieNode);    
                }
            }

            treeCommitter.MemorySize.Should().BeLessThan(512 * 2);
        }
        
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void Dispatcher_will_save_to_db_everything_from_snapshot_blocks(int refCount)
        {
            TrieNode a = new TrieNode(NodeType.Leaf, new byte[0]); // 192B
            a.ResolveKey(null, true);

            MemDb memDb = new MemDb();
            TreeCommitter treeCommitter = new TreeCommitter(memDb, LimboLogs.Instance, 16.MB(), 4);
            
            a.Refs = refCount;
            treeCommitter.Commit(0, a);
            treeCommitter.Commit(1, null);
            treeCommitter.Commit(2, null);
            treeCommitter.Commit(3, null);
            treeCommitter.Commit(4, null);

            memDb[a.Keccak!.Bytes].Should().NotBeNull();
            treeCommitter.IsInMemory(a.Keccak).Should().BeFalse();
        }
        
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void Stays_in_memory_until_persisted(int refCount)
        {
            TrieNode a = new TrieNode(NodeType.Leaf, new byte[0]); // 192B
            a.ResolveKey(null, true);

            MemDb memDb = new MemDb();
            TreeCommitter treeCommitter = new TreeCommitter(memDb, LimboLogs.Instance, 16.MB(), 4);
            
            a.Refs = refCount;
            treeCommitter.Commit(0, a);
            treeCommitter.Commit(1, null);
            treeCommitter.Commit(2, null);
            treeCommitter.Commit(3, null);
            // treeCommitter.Commit(4, null); <- do not persist in this test

            memDb[a.Keccak!.Bytes].Should().BeNull();
            treeCommitter.IsInMemory(a.Keccak).Should().BeTrue();
        }
        
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void Will_get_persisted_on_snapshot_if_referenced(int refCount)
        {
            TrieNode a = new TrieNode(NodeType.Leaf, new byte[0]); // 192B
            a.ResolveKey(null, true);

            MemDb memDb = new MemDb();
            TreeCommitter treeCommitter = new TreeCommitter(memDb, LimboLogs.Instance, 16.MB(), 4);
            
            a.Refs = refCount;
            treeCommitter.Commit(0, null);
            treeCommitter.Commit(1, a);
            treeCommitter.Commit(2, null);
            treeCommitter.Commit(3, null);
            treeCommitter.Commit(4, null);
            treeCommitter.Commit(5, null);
            treeCommitter.Commit(6, null);
            treeCommitter.Commit(7, null);
            treeCommitter.Commit(8, null);

            memDb[a.Keccak!.Bytes].Should().NotBeNull();
            treeCommitter.IsInMemory(a.Keccak).Should().BeFalse();
        }
        
        [Test]
        public void Will_not_get_dropped_on_snapshot_if_unreferenced_in_later_blocks()
        {
            TrieNode a = new TrieNode(NodeType.Leaf, new byte[0]);
            a.ResolveKey(null, true);
            
            TrieNode b = new TrieNode(NodeType.Leaf, new byte[1]);
            b.ResolveKey(null, true);

            MemDb memDb = new MemDb();
            TreeCommitter treeCommitter = new TreeCommitter(memDb, LimboLogs.Instance, 16.MB(), 4);
            
            a.Refs = 1;
            treeCommitter.Commit(0, null);
            treeCommitter.Commit(1, a);
            treeCommitter.Commit(2, null);
            treeCommitter.Commit(3, null);
            treeCommitter.Commit(4, null);
            treeCommitter.Commit(5, null);
            treeCommitter.Commit(6, null);
            // TODO: this is actually a bug since 'a' was referenced from root at the time of block 4
            a.Refs = 0;
            treeCommitter.Commit(7, b); // <- new root
            treeCommitter.Commit(8, null);

            memDb[a.Keccak!.Bytes].Should().NotBeNull();
            treeCommitter.IsInMemory(a.Keccak).Should().BeFalse();
        }
        
        [Test]
        public void Will_get_dropped_on_snapshot_if_it_was_a_transient_node()
        {
            TrieNode a = new TrieNode(NodeType.Leaf, new byte[0]);
            a.ResolveKey(null, true);
            
            TrieNode b = new TrieNode(NodeType.Leaf, new byte[1]);
            b.ResolveKey(null, true);

            MemDb memDb = new MemDb();
            TreeCommitter treeCommitter = new TreeCommitter(memDb, LimboLogs.Instance, 16.MB(), 4);
            
            a.Refs = 1;
            treeCommitter.Commit(0, null);
            treeCommitter.Commit(1, a);
            treeCommitter.Commit(2, null);
            a.Refs = 0;
            treeCommitter.Commit(3, b); // <- new root
            treeCommitter.Commit(4, null);
            treeCommitter.Commit(5, null);
            treeCommitter.Commit(6, null);
            treeCommitter.Commit(7, null);
            treeCommitter.Commit(8, null);

            memDb[a.Keccak!.Bytes].Should().BeNull();
            treeCommitter.IsInMemory(a.Keccak).Should().BeFalse();
        }
    }
}