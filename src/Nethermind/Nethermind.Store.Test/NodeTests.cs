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
            Node node = new Node(NodeType.Branch);
            node.Children[0] = new Node(NodeType.Leaf, TestObject.KeccakA);
            node.Children[1] = new Node(NodeType.Leaf, TestObject.KeccakB);
            PatriciaTree tree = BuildATreeFromNode(node);
            Node decoded = new Node(NodeType.Unknown, node.Keccak);
            decoded.ResolveNode(tree);
            decoded.RlpEncode();
        }

        [Test]
        public void Two_children_store_resolve_encode()
        {
            Node node = new Node(NodeType.Branch);
            node.Children[0] = new Node(NodeType.Leaf, TestObject.KeccakA);
            node.Children[1] = new Node(NodeType.Leaf, TestObject.KeccakB);
            PatriciaTree tree = BuildATreeFromNode(node);
            Node decoded = new Node(NodeType.Unknown, node.Keccak);
            decoded.ResolveNode(tree);
            decoded.RlpEncode();
        }

        [Test]
        public void Two_children_store_resolve_get1_encode()
        {
            Node node = new Node(NodeType.Branch);
            node.Children[0] = new Node(NodeType.Leaf, TestObject.KeccakA);
            node.Children[1] = new Node(NodeType.Leaf, TestObject.KeccakB);
            PatriciaTree tree = BuildATreeFromNode(node);
            Node decoded = new Node(NodeType.Unknown, node.Keccak);
            decoded.ResolveNode(tree);
            Node child0 = decoded.Children[0];
            decoded.RlpEncode();
        }

        [Test]
        public void Two_children_store_resolve_getnull_encode()
        {
            Node node = new Node(NodeType.Branch);
            node.Children[0] = new Node(NodeType.Leaf, TestObject.KeccakA);
            node.Children[1] = new Node(NodeType.Leaf, TestObject.KeccakB);
            PatriciaTree tree = BuildATreeFromNode(node);
            Node decoded = new Node(NodeType.Unknown, node.Keccak);
            decoded.ResolveNode(tree);
            Node child = decoded.Children[3];
            decoded.RlpEncode();
        }

        [Test]
        public void Two_children_store_resolve_update_encode()
        {
            Node node = new Node(NodeType.Branch);
            node.Children[0] = new Node(NodeType.Leaf, TestObject.KeccakA);
            node.Children[1] = new Node(NodeType.Leaf, TestObject.KeccakB);
            PatriciaTree tree = BuildATreeFromNode(node);
            Node decoded = new Node(NodeType.Unknown, node.Keccak);
            decoded.ResolveNode(tree);
            decoded.Children[0] = new Node(NodeType.Leaf, TestObject.KeccakC);
            decoded.RlpEncode();
        }
        
        [Test]
        public void Two_children_store_resolve_updatenull_encode()
        {
            Node node = new Node(NodeType.Branch);
            node.Children[0] = new Node(NodeType.Leaf, TestObject.KeccakA);
            node.Children[1] = new Node(NodeType.Leaf, TestObject.KeccakB);
            PatriciaTree tree = BuildATreeFromNode(node);
            Node decoded = new Node(NodeType.Unknown, node.Keccak);
            decoded.ResolveNode(tree);
            decoded.Children[4] = new Node(NodeType.Leaf, TestObject.KeccakC);
            decoded.Children[5] = new Node(NodeType.Leaf, TestObject.KeccakD);
            decoded.RlpEncode();
        }
        
        [Test]
        public void Two_children_store_resolve_delete_and_add_encode()
        {
            Node node = new Node(NodeType.Branch);
            node.Children[0] = new Node(NodeType.Leaf, TestObject.KeccakA);
            node.Children[1] = new Node(NodeType.Leaf, TestObject.KeccakB);
            PatriciaTree tree = BuildATreeFromNode(node);
            Node decoded = new Node(NodeType.Unknown, node.Keccak);
            decoded.ResolveNode(tree);
            decoded.Children[0] = null;
            decoded.Children[4] = new Node(NodeType.Leaf, TestObject.KeccakC);
            decoded.RlpEncode();
        }

        [Test]
        public void Child_and_value_store_encode()
        {
            Node node = new Node(NodeType.Branch);
            node.Children[0] = new Node(NodeType.Leaf, TestObject.KeccakA);
            node.Value = new byte[] {1, 2, 3};
            PatriciaTree tree = BuildATreeFromNode(node);
            Node decoded = new Node(NodeType.Unknown, node.Keccak);
            decoded.ResolveNode(tree);
            decoded.RlpEncode();
        }

        private static PatriciaTree BuildATreeFromNode(Node node)
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