using System;
using System.Collections.Generic;
using Nevermind.Core.Encoding;
using Nevermind.Core.Sugar;

namespace Nevermind.Store
{
    // move to rocksDB
    public class Db
    {
        private readonly Dictionary<Keccak, byte[]> _db = new Dictionary<Keccak, byte[]>();

        public byte[] this[Keccak key]
        {
            get => _db[key];
            set => _db[key] = value;
        }


        // temp
        public int Count => _db.Count;
    }

    public class PatriciaTree
    {
        public byte[] RlpEncode(Node node)
        {
            LeafNode leaf = node as LeafNode;
            if (leaf != null)
            {
                return RecursiveLengthPrefix.Serialize(leaf.Key, leaf.Value);
            }

            BranchNode branch = node as BranchNode;
            if (branch != null)
            {
                return RecursiveLengthPrefix.Serialize(
                    branch.Nodes[0x0]?.Bytes ?? new byte[]{},
                    branch.Nodes[0x1]?.Bytes ?? new byte[]{},
                    branch.Nodes[0x2]?.Bytes ?? new byte[]{},
                    branch.Nodes[0x3]?.Bytes ?? new byte[]{},
                    branch.Nodes[0x4]?.Bytes ?? new byte[]{},
                    branch.Nodes[0x5]?.Bytes ?? new byte[]{},
                    branch.Nodes[0x6]?.Bytes ?? new byte[]{},
                    branch.Nodes[0x7]?.Bytes ?? new byte[]{},
                    branch.Nodes[0x8]?.Bytes ?? new byte[]{},
                    branch.Nodes[0x9]?.Bytes ?? new byte[]{},
                    branch.Nodes[0xa]?.Bytes ?? new byte[]{},
                    branch.Nodes[0xb]?.Bytes ?? new byte[]{},
                    branch.Nodes[0xc]?.Bytes ?? new byte[]{},
                    branch.Nodes[0xd]?.Bytes ?? new byte[]{},
                    branch.Nodes[0xe]?.Bytes ?? new byte[]{},
                    branch.Nodes[0xf]?.Bytes ?? new byte[]{},
                    branch.Value);
            }

            ExtensionNode extension = node as ExtensionNode;
            if (extension != null)
            {
                return RecursiveLengthPrefix.Serialize(extension.Key, extension.NextNodeHash.Bytes);
            }

            throw new NotImplementedException("Unknown node type");
        }

        public Node RlpDecode(byte[] bytes)
        {
            object[] decoded = (object[])RecursiveLengthPrefix.Deserialize(bytes);
            if (decoded.Length == 17)
            {
                BranchNode branch = new BranchNode();
                for (int i = 0; i < 16; i++)
                {
                    branch.Nodes[i] = decoded[i] == null ? null : new Keccak((byte[])decoded[i]);
                }

                branch.Value = (byte[])decoded[16];
                return branch;
            }

            if (decoded.Length == 2)
            {
                byte[] key = (byte[])decoded[0];
                bool isExtension = (byte)(key[0] & (2 << 4)) == 0;
                if (isExtension)
                {
                    ExtensionNode extension = new ExtensionNode();
                    extension.Key = key;
                    extension.NextNodeHash = new Keccak(
                        (byte[])RecursiveLengthPrefix.Deserialize((byte[])decoded[16]));
                    return extension;
                }

                LeafNode leaf = new LeafNode();
                leaf.Key = key;
                leaf.Value = (byte[])decoded[1];
                return leaf;
            }

            throw new NotImplementedException("Invalid node RLP");
        }

        private readonly Db _db;

        public Keccak RootHash { get; private set; }

        public PatriciaTree(Db db)
        {
            _db = db;
        }

        public PatriciaTree(Keccak rootHash, Db db)
            : this(db)
        {
            RootHash = rootHash;
            byte[] value = _db[RootHash];
            Root = RlpDecode(value);
        }

        public Node Root { get; private set; }

        public void Set(byte[] key, byte[] value)
        {
            byte[] newKey = new HexPrefix(true, Nibbles.FromBytes(key)).ToBytes();
            if (Root == null)
            {
                Initialize(newKey, value);
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
                    if (currentLeaf.Key[i] != newKey[currentIndex])
                    {
                        LeafNode oldLeaf = new LeafNode(new byte[] { }, currentLeaf.Value);
                        // 0 for now
                        //byte[] newLeafKey = new byte[key.Length - currentLeaf.Key.Length];
                        Keccak oldLeafHash = StoreNode(oldLeaf);

                        LeafNode newLeaf = new LeafNode(new byte[] { }, value);
                        // 0 for now
                        //byte[] newLeafKey = new byte[key.Length - currentLeaf.Key.Length];
                        Keccak newLeafHash = StoreNode(newLeaf);

                        BranchNode branch = new BranchNode();
                        branch.Value = currentLeaf.Value;
                        branch.Nodes[newKey[currentIndex]] = newLeafHash;
                        branch.Nodes[currentLeaf.Key[i]] = oldLeafHash;
                        Keccak branchHash = StoreNode(branch);

                        ExtensionNode extension = new ExtensionNode();
                        extension.Key = currentLeaf.Key;
                        extension.NextNodeHash = branchHash;
                        Keccak extensionHash = StoreNode(extension);

                        if (previousNode == null)
                        {
                            Root = extension;
                            RootHash = extensionHash;
                        }

                        previousNode = currentNode;
                        previousBranchIndex = -1;
                    }
                    else if (currentIndex == newKey.Length - 1 && newKey.Length == currentLeaf.Key.Length && currentLeaf.Key[i] == newKey[currentIndex])
                    {
                        // if same
                        if (Bytes.UnsafeCompare(currentLeaf.Value, value))
                        {
                            return;
                        }

                        LeafNode newLeaf = new LeafNode(newKey, value);
                        // 0 for now
                        //byte[] newLeafKey = new byte[key.Length - currentLeaf.Key.Length];
                        Keccak newLeafHash = StoreNode(newLeaf);
                        if (previousNode == null)
                        {
                            Root = newLeaf;
                            RootHash = newLeafHash;
                        }
                    }

                    currentIndex++;
                }
            }
        }

        private void Initialize(byte[] hexPrefix, byte[] value)
        {
            LeafNode node = new LeafNode(hexPrefix, value);
            byte[] nodeRlp = RlpEncode(node);
            Keccak nodeHash = Keccak.Compute(nodeRlp);
            _db[nodeHash] = nodeRlp;
            Root = node;
            RootHash = nodeHash;
        }


        private Keccak StoreNode(Node node)
        {
            byte[] newLeafRlp = RlpEncode(node);
            Keccak newLeafKeccak = Keccak.Compute(newLeafRlp);
            _db[newLeafKeccak] = newLeafRlp;
            return newLeafKeccak;
        }
    }
}