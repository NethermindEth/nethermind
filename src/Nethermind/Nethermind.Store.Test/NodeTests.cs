using Nethermind.Core.Encoding;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Store.Test
{
    [TestFixture]
    public class NodeTest
    {
        [Test]
        public void Two_children_store_encode()
        {
            TrieNode node = new TrieNode(NodeType.Branch);
            node.SetChild(0, new TrieNode(NodeType.Leaf, TestObject.KeccakA));
            node.SetChild(1, new TrieNode(NodeType.Leaf, TestObject.KeccakB));
            PatriciaTree tree = BuildATreeFromNode(node);
            TrieNode decoded = new TrieNode(NodeType.Unknown, node.Keccak);
            decoded.ResolveNode(tree);
            decoded.RlpEncode();
        }

        [Test]
        public void Two_children_store_resolve_encode()
        {
            TrieNode node = new TrieNode(NodeType.Branch);
            node.SetChild(0, new TrieNode(NodeType.Leaf, TestObject.KeccakA));
            node.SetChild(1, new TrieNode(NodeType.Leaf, TestObject.KeccakB));
            PatriciaTree tree = BuildATreeFromNode(node);
            TrieNode decoded = new TrieNode(NodeType.Unknown, node.Keccak);
            decoded.ResolveNode(tree);
            decoded.RlpEncode();
        }

        [Test]
        public void Two_children_store_resolve_get1_encode()
        {
            TrieNode node = new TrieNode(NodeType.Branch);
            node.SetChild(0, new TrieNode(NodeType.Leaf, TestObject.KeccakA));
            node.SetChild(1, new TrieNode(NodeType.Leaf, TestObject.KeccakB));
            PatriciaTree tree = BuildATreeFromNode(node);
            TrieNode decoded = new TrieNode(NodeType.Unknown, node.Keccak);
            decoded.ResolveNode(tree);
            TrieNode child0 = decoded.GetChild(0);
            decoded.RlpEncode();
        }

        [Test]
        public void Two_children_store_resolve_getnull_encode()
        {
            TrieNode node = new TrieNode(NodeType.Branch);
            node.SetChild(0, new TrieNode(NodeType.Leaf, TestObject.KeccakA));
            node.SetChild(1, new TrieNode(NodeType.Leaf, TestObject.KeccakB));
            PatriciaTree tree = BuildATreeFromNode(node);
            TrieNode decoded = new TrieNode(NodeType.Unknown, node.Keccak);
            decoded.ResolveNode(tree);
            TrieNode child = decoded.GetChild(3);
            decoded.RlpEncode();
        }

        [Test]
        public void Two_children_store_resolve_update_encode()
        {
            TrieNode node = new TrieNode(NodeType.Branch);
            node.SetChild(0, new TrieNode(NodeType.Leaf, TestObject.KeccakA));
            node.SetChild(1, new TrieNode(NodeType.Leaf, TestObject.KeccakB));
            PatriciaTree tree = BuildATreeFromNode(node);
            TrieNode decoded = new TrieNode(NodeType.Unknown, node.Keccak);
            decoded.ResolveNode(tree);
            decoded.SetChild(0, new TrieNode(NodeType.Leaf, TestObject.KeccakC));
            decoded.RlpEncode();
        }
        
        [Test]
        public void Two_children_store_resolve_updatenull_encode()
        {
            TrieNode node = new TrieNode(NodeType.Branch);
            node.SetChild(0, new TrieNode(NodeType.Leaf, TestObject.KeccakA));
            node.SetChild(1, new TrieNode(NodeType.Leaf, TestObject.KeccakB));
            PatriciaTree tree = BuildATreeFromNode(node);
            TrieNode decoded = new TrieNode(NodeType.Unknown, node.Keccak);
            decoded.ResolveNode(tree);
            decoded.SetChild(4, new TrieNode(NodeType.Leaf, TestObject.KeccakC));
            decoded.SetChild(5, new TrieNode(NodeType.Leaf, TestObject.KeccakD));
            decoded.RlpEncode();
        }
        
        [Test]
        public void Two_children_store_resolve_delete_and_add_encode()
        {
            TrieNode node = new TrieNode(NodeType.Branch);
            node.SetChild(0, new TrieNode(NodeType.Leaf, TestObject.KeccakA));
            node.SetChild(1, new TrieNode(NodeType.Leaf, TestObject.KeccakB));
            PatriciaTree tree = BuildATreeFromNode(node);
            TrieNode decoded = new TrieNode(NodeType.Unknown, node.Keccak);
            decoded.ResolveNode(tree);
            decoded.SetChild(0, null);
            decoded.SetChild(4, new TrieNode(NodeType.Leaf, TestObject.KeccakC));
            decoded.RlpEncode();
        }

        [Test]
        public void Child_and_value_store_encode()
        {
            TrieNode node = new TrieNode(NodeType.Branch);
            node.SetChild(0, new TrieNode(NodeType.Leaf, TestObject.KeccakA));
            node.Value = new byte[] {1, 2, 3};
            PatriciaTree tree = BuildATreeFromNode(node);
            TrieNode decoded = new TrieNode(NodeType.Unknown, node.Keccak);
            decoded.ResolveNode(tree);
            decoded.RlpEncode();
        }

        private static PatriciaTree BuildATreeFromNode(TrieNode node)
        {
            Rlp rlp = node.RlpEncode();
            node.ResolveKey(true);

            MemDb memDb = new MemDb();
            memDb[node.Keccak.Bytes] = rlp.Bytes;

            PatriciaTree tree = new PatriciaTree(memDb, node.Keccak, false);
            return tree;
        }
    }
}