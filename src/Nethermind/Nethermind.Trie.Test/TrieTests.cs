using FluentAssertions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using NUnit.Framework;

namespace Nethermind.Trie.Test
{
    [TestFixture]
    public class TrieTests
    {
        [Test]
        public void Set_single_leaf_and_count_memory()
        {
            MemDb memDb = new MemDb();
            PatriciaTree tree = new PatriciaTree(memDb);
            tree.Set(TestItem.KeccakA.Bytes, new byte[100]);
            tree.Root.NodeType.Should().Be(NodeType.Leaf);
            tree.Commit();
            tree.Root.NodeType.Should().Be(NodeType.Leaf);
        }
    }
}