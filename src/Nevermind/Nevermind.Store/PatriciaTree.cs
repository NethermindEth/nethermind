using System;
using Nevermind.Core.Encoding;
using Nevermind.Core.Sugar;

namespace Nevermind.Store
{
    public class PatriciaTree
    {
        private readonly Db _db;

        public PatriciaTree(Db db)
        {
            _db = db;
        }

        public PatriciaTree(Keccak rootHash, Db db)
            : this(db)
        {
            RootHash = rootHash;
            Rlp rootRlp = new Rlp(_db[rootHash]);
            Root = RlpDecode(rootRlp);
        }

        public Keccak RootHash { get; private set; }

        internal Node Root { get; private set; }

        internal Rlp RlpEncode(Node node)
        {
            LeafNode leaf = node as LeafNode;
            if (leaf != null)
            {
                return Rlp.Serialize(leaf.Key, leaf.Value);
            }

            BranchNode branch = node as BranchNode;
            if (branch != null)
            {
                return Rlp.Serialize(
                    branch.Nodes[0x0]?.Bytes ?? new byte[] { },
                    branch.Nodes[0x1]?.Bytes ?? new byte[] { },
                    branch.Nodes[0x2]?.Bytes ?? new byte[] { },
                    branch.Nodes[0x3]?.Bytes ?? new byte[] { },
                    branch.Nodes[0x4]?.Bytes ?? new byte[] { },
                    branch.Nodes[0x5]?.Bytes ?? new byte[] { },
                    branch.Nodes[0x6]?.Bytes ?? new byte[] { },
                    branch.Nodes[0x7]?.Bytes ?? new byte[] { },
                    branch.Nodes[0x8]?.Bytes ?? new byte[] { },
                    branch.Nodes[0x9]?.Bytes ?? new byte[] { },
                    branch.Nodes[0xa]?.Bytes ?? new byte[] { },
                    branch.Nodes[0xb]?.Bytes ?? new byte[] { },
                    branch.Nodes[0xc]?.Bytes ?? new byte[] { },
                    branch.Nodes[0xd]?.Bytes ?? new byte[] { },
                    branch.Nodes[0xe]?.Bytes ?? new byte[] { },
                    branch.Nodes[0xf]?.Bytes ?? new byte[] { },
                    branch.Value ?? new byte[] { });
            }

            ExtensionNode extension = node as ExtensionNode;
            if (extension != null)
            {
                return Rlp.Serialize(extension.Key, extension.NextNode.Bytes);
            }

            throw new NotImplementedException("Unknown node type");
        }

        internal Node RlpDecode(Rlp bytes)
        {
            object[] decoded = (object[]) Rlp.Deserialize(bytes);
            if (decoded.Length == 17)
            {
                BranchNode branch = new BranchNode();
                for (int i = 0; i < 16; i++)
                {
                    byte[] nodeBytes = (byte[]) decoded[i];
                    branch.Nodes[i] = DecodeKeccakOrRlp(nodeBytes);
                }

                branch.Value = (byte[]) decoded[16];
                return branch;
            }

            if (decoded.Length == 2)
            {
                byte[] key = (byte[]) decoded[0];
                bool isExtension = (byte) (key[0] & (2 << 4)) == 0;
                if (isExtension)
                {
                    ExtensionNode extension = new ExtensionNode();
                    extension.Key = key;
                    byte[] nodeBytes = (byte[]) Rlp.Deserialize(new Rlp((byte[]) decoded[16]));
                    extension.NextNode = DecodeKeccakOrRlp(nodeBytes);
                    return extension;
                }

                LeafNode leaf = new LeafNode();
                leaf.Key = key;
                leaf.Value = (byte[]) decoded[1];
                return leaf;
            }

            throw new NotImplementedException("Invalid node RLP");
        }

        private static KeccakOrRlp DecodeKeccakOrRlp(byte[] nodeBytes)
        {
            KeccakOrRlp keccakOrRlp = null;
            if (nodeBytes.Length != 0)
            {
                keccakOrRlp = nodeBytes.Length == 32
                    ? new KeccakOrRlp(new Keccak(nodeBytes))
                    : new KeccakOrRlp(new Rlp(nodeBytes));
            }
            return keccakOrRlp;
        }

        public void Set(byte[] rawKey, Rlp rlp)
        {
            Set(rawKey, rlp.Bytes);
        }

        public void Set(byte[] rawKey, byte[] value)
        {
            byte[] hpKey = new HexPrefix(true, Nibbles.FromBytes(rawKey)).ToBytes();
            if (Root == null)
            {
                StoreNode(new LeafNode(hpKey, value), true);
                return;
            }

            Node previousNode = null;
            Node currentNode = Root;
            int previousBranchIndex = -1;
            int currentIndex = 0;
            LeafNode currentLeaf = currentNode as LeafNode;
            if (currentLeaf != null)
            {
                for (int i = 0; i < currentLeaf.Key.Length; i++)
                {
                    if (currentLeaf.Key[i] != hpKey[currentIndex])
                    {
                        LeafNode oldLeaf = new LeafNode(new byte[] { }, currentLeaf.Value);
                        // 0 for now
                        //byte[] newLeafKey = new byte[rawKey.Length - currentLeaf.Key.Length];
                        KeccakOrRlp oldLeafHash = StoreNode(oldLeaf);

                        LeafNode newLeaf = new LeafNode(new byte[] { }, value);
                        // 0 for now
                        //byte[] newLeafKey = new byte[rawKey.Length - currentLeaf.Key.Length];
                        KeccakOrRlp newLeafHash = StoreNode(newLeaf);

                        BranchNode branch = new BranchNode();
                        branch.Nodes[hpKey[currentIndex]] = newLeafHash;
                        branch.Nodes[currentLeaf.Key[i]] = oldLeafHash;
                        KeccakOrRlp branchHash = StoreNode(branch);

                        ExtensionNode extension = new ExtensionNode();
                        extension.Key = currentLeaf.Key;
                        extension.NextNode = branchHash;
                        StoreNode(extension, previousNode == null);

                        previousNode = currentNode;
                        previousBranchIndex = -1;
                    }
                    else if (currentIndex == hpKey.Length - 1 && hpKey.Length == currentLeaf.Key.Length &&
                             currentLeaf.Key[i] == hpKey[currentIndex])
                    {
                        // if same
                        if (Bytes.UnsafeCompare(currentLeaf.Value, value))
                        {
                            return;
                        }

                        LeafNode newLeaf = new LeafNode(hpKey, value);
                        // 0 for now
                        //byte[] newLeafKey = new byte[rawKey.Length - currentLeaf.Key.Length];
                        StoreNode(newLeaf, previousNode == null);
                    }

                    currentIndex++;
                }
            }
        }

        ////private class Position
        ////{
        ////    public Position(Node node, int index, bool isNewNode)
        ////    {
        ////        Node = node;
        ////        IndexWithinNode = index;
        ////        IsNewNode = isNewNode;
        ////    }

        ////    public bool IsNewNode { get; set; }
        ////    public Node Node { get; set; }
        ////    public int IndexWithinNode { get; set; }
        ////}

        ////private Position GetNext(LeafNode currentNode, int indexWithinNode, byte nibble, bool create)
        ////{
        ////    if (indexWithinNode == currentNode.Key.Length - 1)
        ////    {
        ////        return null; // extend into branch with a leaf and value, leaf with nibble, return new leaf 
        ////    }

        ////    if (currentNode.Key[indexWithinNode + 1] == nibble)
        ////    {
        ////        return new Position(currentNode, indexWithinNode++);
        ////    }

        ////    if (currentNode.Key[indexWithinNode + 1] != nibble)
        ////    {
        ////        return null; //extend into branch with two leaf nodes, one finalized, return new lead
        ////    }

        ////    throw new InvalidOperationException();
        ////}

        ////private Position GetNext(ExtensionNode currentNode, int indexWithinNode, byte nibble, bool create)
        ////{
        ////    if (indexWithinNode == currentNode.Key.Length - 1)
        ////    {
        ////        Node nextNode = RlpDecode(_db[currentNode.NextNode]);
        ////        //...
        ////        return null; // extend into branch with a leaf and value, leaf with nibble, return new leaf 
        ////    }

        ////    if (currentNode.Key[indexWithinNode + 1] == nibble)
        ////    {
        ////        return new Position(currentNode, indexWithinNode++, false);
        ////    }

        ////    if (currentNode.Key[indexWithinNode + 1] != nibble)
        ////    {
        ////        ExtensionNode extensionNode = new ExtensionNode();
        ////        return null; //extend into branch with two leaf nodes, one finalized, return new lead
        ////    }

        ////    throw new InvalidOperationException();
        ////}

        ////private Position GetNext(BranchNode currentNode, int indexWithinNode, byte nibble, bool create)
        ////{
        ////    if (indexWithinNode != 0)
        ////    {
        ////        throw new InvalidOperationException();
        ////    }

        ////    if (currentNode.Nodes[nibble] == null)
        ////    {
        ////        if (!create)
        ////        {
        ////            return null;
        ////        }

        ////        LeafNode newLeaf = new LeafNode();
        ////        newLeaf.Key = new byte[] { nibble };
        ////        return new Position(newLeaf, 1, true);
        ////    }

        ////    Node nextNode = RlpDecode(_db[currentNode.Nodes[nibble]]);
        ////    LeafNode nextLeaf = nextNode as LeafNode;
        ////    if (nextLeaf != null)
        ////    {
        ////        return GetNext(nextLeaf, indexWithinNode, nibble, create);
        ////    }

        ////    ExtensionNode nextExtension = nextNode as ExtensionNode;
        ////    if (nextExtension != null)
        ////    {
        ////        return GetNext(nextExtension, indexWithinNode, nibble, create);
        ////    }

        ////    BranchNode nextBranch = nextNode as BranchNode;
        ////    if (nextBranch != null)
        ////    {
        ////        return GetNext(nextBranch, indexWithinNode, nibble, create);
        ////    }

        ////    throw new InvalidOperationException();
        ////}

        internal Node GetNode(KeccakOrRlp keccakOrRlp)
        {
            Rlp rlp = new Rlp(keccakOrRlp.IsKeccak ? _db[keccakOrRlp.GetKeccakOrComputeFromRlp()] : keccakOrRlp.Bytes);
            return RlpDecode(rlp);
        }

        private KeccakOrRlp StoreNode(Node node, bool isRoot = false)
        {
            Rlp rlp = RlpEncode(node);
            KeccakOrRlp key = new KeccakOrRlp(rlp);
            if (key.IsKeccak || isRoot)
            {
                _db[key.GetKeccakOrComputeFromRlp()] = rlp.Bytes;
            }

            if (isRoot)
            {
                Root = node;
                RootHash = key.GetKeccakOrComputeFromRlp();
            }

            return key;
        }
    }
}