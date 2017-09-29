using System;
using Nevermind.Core.Encoding;

namespace Nevermind.Store
{
    // I guess it is a very slow to Keccak-heavy implementation, the first one to pass tests
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
            return keccakOrRlp == null ? Rlp.OfEmptyString : keccakOrRlp.GetOrEncodeRlp();
        }

        internal static Rlp RlpEncode(Node node)
        {
            if (node is LeafNode leaf)
            {
                Rlp result = Rlp.Serialize(leaf.Key.ToBytes(), leaf.Value);
                return result;
            }

            if (node is BranchNode branch)
            {
                // Geth encoded a structure of nodes so child nodes are actual objects and not RLP of items,
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

            if (node is ExtensionNode extension)
            {
                return Rlp.Serialize(extension.Key.ToBytes(), RlpEncode(extension.NextNode));
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
                    branch.Nodes[i] = DecodeChildNode(decoded[i]);
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
                    extension.NextNode = DecodeChildNode(decoded[1]);
                    return extension;
                }

                LeafNode leaf = new LeafNode();
                leaf.Key = key;
                leaf.Value = (byte[]) decoded[1];
                return leaf;
            }

            throw new NotImplementedException("Invalid node RLP");
        }

        private static KeccakOrRlp DecodeChildNode(object deserialized)
        {
            if (deserialized is object[] nodeSequence)
            {
                return new KeccakOrRlp(Rlp.Serialize(nodeSequence));
            }

            if (deserialized is byte[] bytes)
            {
                return bytes.Length == 0 ? null : new KeccakOrRlp(new Keccak(bytes));
            }

            throw new InvalidOperationException("Invalid child node RLP");
        }

        public void Set(byte[] rawKey, Rlp rlp)
        {
            Set(rawKey, rlp.Bytes);
        }

        public virtual void Set(byte[] rawKey, byte[] value)
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

            if (node is ExtensionNode extension)
            {
                DeleteNode(extension.NextNode, true);
                _db.Delete(hash.GetOrComputeKeccak());
            }

            if (node is BranchNode branch)
            {
                foreach (KeccakOrRlp subnode in branch.Nodes)
                {
                    DeleteNode(subnode, true);
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