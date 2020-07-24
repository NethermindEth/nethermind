using FluentAssertions;
using Nethermind.Core.Extensions;
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
            TrieNode trieNode = new TrieNode(NodeType.Unknown); // 56B
            
            TreeCommitter treeCommitter = new TreeCommitter(new MemDb(), LimboLogs.Instance, 1.MB());
            treeCommitter.Commit(1234, trieNode);
            treeCommitter.MemorySize.Should().Be(
                96 /* committer */ +
                88 /* block package */ +
                48 /* linked list node size */ +
                trieNode.GetMemorySize(false));
        }
        
        [Test]
        public void Memory_with_two_nodes_is_344()
        {
            TrieNode trieNode = new TrieNode(NodeType.Unknown); // 56B
            
            TreeCommitter treeCommitter = new TreeCommitter(new MemDb(), LimboLogs.Instance, 1.MB());
            treeCommitter.Commit(1234, trieNode);
            treeCommitter.Commit(1234, trieNode);
            treeCommitter.MemorySize.Should().Be(
                96 /* committer */ +
                88 /* block package */ +
                48 /* linked list node size */ +
                2 * trieNode.GetMemorySize(false));
        }
        
        [Test]
        public void Memory_with_two_times_two_nodes_is_592()
        {
            TrieNode trieNode = new TrieNode(NodeType.Unknown); // 56B
            
            TreeCommitter treeCommitter = new TreeCommitter(new MemDb(), LimboLogs.Instance, 1.MB());
            treeCommitter.Commit(1234, trieNode);
            treeCommitter.Commit(1234, trieNode);
            treeCommitter.Commit(1235, trieNode);
            treeCommitter.Commit(1235, trieNode);
            treeCommitter.MemorySize.Should().Be(
                96 /* committer */ +
                2 * 88 /* block package */ +
                2 * 48 /* linked list node size */ +
                4 * trieNode.GetMemorySize(false));
        }
        
        [Test]
        public void Dispatcher_will_try_to_clear_memory()
        {
            TrieNode trieNode = new TrieNode(NodeType.Leaf, new byte[0]); // 192B
            trieNode.ResolveKey(true);

            TreeCommitter treeCommitter = new TreeCommitter(new MemDb(), LimboLogs.Instance, 640);
            treeCommitter.Commit(1234, trieNode);
            treeCommitter.Commit(1234, trieNode);
            treeCommitter.Commit(1235, trieNode);
            treeCommitter.Commit(1235, trieNode);
            treeCommitter.MemorySize.Should().Be(
                96 /* committer */ +
                1 * 88 /* block package */ +
                1 * 48 /* linked list node size */ +
                2 * trieNode.GetMemorySize(false));
        }
        
        [Test]
        public void Dispatcher_will_try_to_clear_memory_the_soonest_possible()
        {
            TrieNode trieNode = new TrieNode(NodeType.Leaf, new byte[0]); // 192B
            trieNode.ResolveKey(true);

            TreeCommitter treeCommitter = new TreeCommitter(new MemDb(), LimboLogs.Instance, 512);
            treeCommitter.Commit(1234, trieNode);
            treeCommitter.Commit(1234, trieNode);
            treeCommitter.Commit(1235, trieNode);
            treeCommitter.Commit(1235, trieNode);
            treeCommitter.MemorySize.Should().Be(
                96 /* committer */ +
                1 * 88 /* block package */ +
                1 * 48 /* linked list node size */ +
                2 * trieNode.GetMemorySize(false));
        }
    }
}