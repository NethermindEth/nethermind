using System;
using System.Diagnostics;
using Nevermind.Core;
using Nevermind.Core.Crypto;
using Nevermind.Core.Encoding;
using Nevermind.Core.Extensions;

namespace Nevermind.Store
{
    // I guess it is a very slow and Keccak-heavy implementation, the first one to pass tests
    [DebuggerDisplay("{RootHash}")]
    public class PatriciaTree
    {
        /// <summary>
        ///     0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421
        /// </summary>
        public static readonly Keccak EmptyTreeHash = Keccak.EmptyTreeHash;

        protected InMemoryDb _db;

        internal Node Root;

        public PatriciaTree(InMemoryDb db)
        {
            _db = db;
        }

        public PatriciaTree(Keccak rootHash, InMemoryDb db)
            : this(db)
        {
            RootHash = rootHash;
            if (RootHash == Keccak.EmptyTreeHash)
            {
                Root = null;
            }
            else
            {
                Rlp rootRlp = new Rlp(_db[RootHash]);
                Root = RlpDecode(rootRlp);
            }
        }

        public Keccak RootHash { get; internal set; } = EmptyTreeHash;

        private static Rlp RlpEncode(KeccakOrRlp keccakOrRlp)
        {
            return keccakOrRlp == null ? Rlp.OfEmptyByteArray : keccakOrRlp.GetOrEncodeRlp();
        }

        internal static Rlp RlpEncode(Node node)
        {
            if (node is LeafNode leaf)
            {
                Rlp result = Rlp.Encode(leaf.Key.ToBytes(), leaf.Value);
                return result;
            }

            if (node is BranchNode branch)
            {
                // Geth encoded a structure of nodes so child nodes are actual objects and not RLP of items,
                // hence when RLP encoding nodes are not byte arrays but actual objects of format byte[][2] or their Keccak
                Rlp result = Rlp.Encode(
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
                return Rlp.Encode(extension.Key.ToBytes(), RlpEncode(extension.NextNode));
            }

            throw new InvalidOperationException("Unknown node type");
        }

        internal static Node RlpDecode(Rlp bytes)
        {
            object[] decoded = (object[])Rlp.Decode(bytes);
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

            throw new InvalidOperationException("Invalid node RLP");
        }

        private static KeccakOrRlp DecodeChildNode(object deserialized)
        {
            if (deserialized is object[] nodeSequence)
            {
                return new KeccakOrRlp(Rlp.Encode(nodeSequence));
            }

            if (deserialized is byte[] bytes)
            {
                return bytes.Length == 0 ? null : new KeccakOrRlp(new Keccak(bytes));
            }

            throw new InvalidOperationException("Invalid child node RLP");
        }

        public void Set(Nibble[] nibbles, Rlp rlp)
        {
            Set(nibbles, rlp.Bytes);
        }

        public virtual void Set(Nibble[] nibbles, byte[] value)
        {
            new TreeOperation(this, nibbles, value, true).Run();
        }

        public byte[] Get(byte[] rawKey)
        {
            return new TreeOperation(this, Nibbles.FromBytes(rawKey), null, false).Run();
        }

        public void Set(byte[] rawKey, byte[] value)
        {
            Set(Nibbles.FromBytes(rawKey), value);
        }

        public void Set(byte[] rawKey, Rlp value)
        {
            Set(Nibbles.FromBytes(rawKey), value == null ? new byte[0] : value.Bytes);
        }

        internal Node GetNode(KeccakOrRlp keccakOrRlp)
        {
            Rlp rlp = null;
            try
            {

            
            rlp = new Rlp(keccakOrRlp.IsKeccak ? _db[keccakOrRlp.GetOrComputeKeccak()] : keccakOrRlp.Bytes);
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
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
            if (isRoot && node == null)
            {
                Root = null;
                _db.Delete(RootHash);
                RootHash = EmptyTreeHash;
                return new KeccakOrRlp(EmptyTreeHash);
            }

            if (node == null)
            {
                return null;
            }

            Rlp rlp = RlpEncode(node);
            KeccakOrRlp key = new KeccakOrRlp(rlp);
            if (key.IsKeccak || isRoot)
            {
                Keccak keyKeccak = key.GetOrComputeKeccak();
                _db[keyKeccak] = rlp.Bytes;

                if (isRoot)
                {
                    Root = node;
                    RootHash = keyKeccak;
                }
            }

            return key;
        }

        public void PrintDbContent()
        {
            _db.Print(Console.WriteLine);
        }
    }
}