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

        public Keccak RootHash { get; internal set; }

        internal Node Root { get; set; }

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
            byte[] hexPrefix = new HexPrefix(true, Nibbles.FromBytes(rawKey)).ToBytes();
            new TreeUpdate(this, hexPrefix, value).Run();
        }

        internal Node GetNode(KeccakOrRlp keccakOrRlp)
        {
            Rlp rlp = new Rlp(keccakOrRlp.IsKeccak ? _db[keccakOrRlp.GetKeccakOrComputeFromRlp()] : keccakOrRlp.Bytes);
            return RlpDecode(rlp);
        }

        internal KeccakOrRlp StoreNode(Node node, bool isRoot = false)
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