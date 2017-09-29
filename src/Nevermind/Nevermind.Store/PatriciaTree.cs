using System;
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

        public Keccak RootHash { get; internal set; } = Keccak.OfAnEmptyString;

        internal Node Root { get; set; }

        private static Rlp RlpEncode(KeccakOrRlp keccakOrRlp)
        {
            if (keccakOrRlp == null)
            {
                return Rlp.OfEmptyString;
            }

            return keccakOrRlp.GetOrEncodeRlp();
        }

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
                // Geth encoded a structure of nodes where child nodes are not rlp but actual objects,
                // hence when RLP encoding nodes are not byte arrays but actual objects of format byte[][2] or their Keccak
                Rlp result = Rlp.Serialize(
                    RlpEncode(branch.Nodes[0x0]),
                    RlpEncode(branch.Nodes[0x1]),
                    RlpEncode(branch.Nodes[0x2]),
                    RlpEncode(branch.Nodes[0x3]),
                    RlpEncode(branch.Nodes[0x4]),
                    RlpEncode(branch.Nodes[0x5]),
                    RlpEncode(branch.Nodes[0x6]),
                    RlpEncode(branch.Nodes[0x7]),
                    RlpEncode(branch.Nodes[0x8]),
                    RlpEncode(branch.Nodes[0x9]),
                    RlpEncode(branch.Nodes[0xa]),
                    RlpEncode(branch.Nodes[0xb]),
                    RlpEncode(branch.Nodes[0xc]),
                    RlpEncode(branch.Nodes[0xd]),
                    RlpEncode(branch.Nodes[0xe]),
                    RlpEncode(branch.Nodes[0xf]),
                    branch.Value ?? new byte[0]);
                return result;
            }

            ExtensionNode extension = node as ExtensionNode;
            if (extension != null)
            {
                return Rlp.Serialize(extension.Key.ToBytes(), RlpEncode(extension.NextNode));
            }

            throw new NotImplementedException("Unknown node type");
        }

        internal static Node RlpDecode(Rlp bytes)
        {
            object[] decoded = (object[])Rlp.Deserialize(bytes);
            if (decoded.Length == 17)
            {
                BranchNode branch = new BranchNode();
                for (int i = 0; i < 16; i++)
                {
                    branch.Nodes[i] = DecodeChildNode(decoded[i]);
                }

                branch.Value = (byte[])decoded[16];
                return branch;
            }

            if (decoded.Length == 2)
            {
                HexPrefix key = HexPrefix.FromBytes((byte[])decoded[0]);
                bool isExtension = key.IsExtension;
                if (isExtension)
                {
                    ExtensionNode extension = new ExtensionNode();
                    extension.Key = key;
                    extension.NextNode = DecodeChildNode(decoded[1]);
                    return extension;
                }

                LeafNode leaf = new LeafNode();
                leaf.Key = key;
                leaf.Value = (byte[])decoded[1];
                return leaf;
            }

            throw new NotImplementedException("Invalid node RLP");
        }

        private static KeccakOrRlp DecodeChildNode(object deserialized)
        {
            object[] nodeSequence = deserialized as object[];
            if (nodeSequence != null)
            {
                return new KeccakOrRlp(Rlp.Serialize(nodeSequence));
            }

            byte[] bytes = deserialized as byte[];
            if (bytes == null || (bytes.Length != 32 && bytes.Length != 0))
            {
                throw new InvalidOperationException("Invalid child node RLP");
            }

            if (bytes.Length == 0)
            {
                return null;
            }

            return new KeccakOrRlp(new Keccak((byte[])deserialized));
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
            Rlp rlp = new Rlp(keccakOrRlp.IsKeccak ? _db[keccakOrRlp.GetOrComputeKeccak()] : keccakOrRlp.Bytes);
            return RlpDecode(rlp);
        }

        internal void DeleteNode(KeccakOrRlp hash, bool ignoreChildren = false)
        {
            if (hash == null || !hash.IsKeccak)
            {
                return;
            }

            Keccak thisNodeKeccak = hash.GetOrComputeKeccak();
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
                _db.Delete(hash.GetOrComputeKeccak());
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
                _db[key.GetOrComputeKeccak()] = rlp.Bytes;
            }

            if (isRoot)
            {
                Root = node;
                RootHash = key.GetOrComputeKeccak();
            }

            return key;
        }
    }
}