using System;
using System.Diagnostics;
using Nevermind.Core.Encoding;

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

        internal static Rlp RlpEncode(Node node)
        {
            LeafNode leaf = node as LeafNode;
            if (leaf != null)
            {
                Rlp result = Rlp.Serialize(leaf.Key.ToBytes(), leaf.Value);
                return result;
            }

            BranchNode branch = node as BranchNode;
            if (branch != null)
            {
                Rlp result = Rlp.Serialize(
                    branch.Nodes[0x0]?.Bytes ?? new byte[0],
                    branch.Nodes[0x1]?.Bytes ?? new byte[0],
                    branch.Nodes[0x2]?.Bytes ?? new byte[0],
                    branch.Nodes[0x3]?.Bytes ?? new byte[0],
                    branch.Nodes[0x4]?.Bytes ?? new byte[0],
                    branch.Nodes[0x5]?.Bytes ?? new byte[0],
                    branch.Nodes[0x6]?.Bytes ?? new byte[0],
                    branch.Nodes[0x7]?.Bytes ?? new byte[0],
                    branch.Nodes[0x8]?.Bytes ?? new byte[0],
                    branch.Nodes[0x9]?.Bytes ?? new byte[0],
                    branch.Nodes[0xa]?.Bytes ?? new byte[0],
                    branch.Nodes[0xb]?.Bytes ?? new byte[0],
                    branch.Nodes[0xc]?.Bytes ?? new byte[0],
                    branch.Nodes[0xd]?.Bytes ?? new byte[0],
                    branch.Nodes[0xe]?.Bytes ?? new byte[0],
                    branch.Nodes[0xf]?.Bytes ?? new byte[0],
                    branch.Value ?? new byte[0]);
                return result;
            }

            ExtensionNode extension = node as ExtensionNode;
            if (extension != null)
            {
                return Rlp.Serialize(extension.Key.ToBytes(), extension.NextNode.Bytes);
            }

            throw new NotImplementedException("Unknown node type");
        }

        internal static Node RlpDecode(Rlp bytes)
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
                HexPrefix key = HexPrefix.FromBytes((byte[]) decoded[0]);
                bool isExtension = key.IsExtension;
                if (isExtension)
                {
                    ExtensionNode extension = new ExtensionNode();
                    extension.Key = key;
                    byte[] nodeBytes = (byte[])decoded[1];
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
            new TreeUpdate(this, rawKey, value).Run();
        }

        internal Node GetNode(KeccakOrRlp keccakOrRlp)
        {
            Rlp rlp = new Rlp(keccakOrRlp.IsKeccak ? _db[keccakOrRlp.GetKeccakOrComputeFromRlp()] : keccakOrRlp.Bytes);
            return RlpDecode(rlp);
        }

        internal void DeleteNode(KeccakOrRlp hash, bool ignoreChildren = false)
        {
            if (hash == null || ! hash.IsKeccak)
            {
                return;
            }

            Keccak thisNodeKeccak = hash.GetKeccakOrComputeFromRlp();
            Node node = ignoreChildren ? null : RlpDecode(new Rlp(_db[thisNodeKeccak]));
            _db.Delete(thisNodeKeccak);

            if (ignoreChildren)
            {
                return;
            }
            
            ExtensionNode extension = node as ExtensionNode;
            if (extension != null)
            {
                DeleteNode(extension.NextNode);
                _db.Delete(hash.GetKeccakOrComputeFromRlp());
            }

            BranchNode branch = node as BranchNode;
            if (branch != null)
            {
                foreach (KeccakOrRlp subnode in branch.Nodes)
                {
                    DeleteNode(subnode);
                }
            }
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