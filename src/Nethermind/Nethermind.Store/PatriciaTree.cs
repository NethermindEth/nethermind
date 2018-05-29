/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;

namespace Nethermind.Store
{
    [DebuggerDisplay("{RootHash}")]
    public class PatriciaTree
    {
        public void Commit(bool wrapInBatch = true)
        {
            if (RootRef == null)
            {
                return;
            }

            if (RootRef.IsDirty)
            {
                Commit(RootRef, true);

                // reset objects
                Keccak keccak = RootRef.KeccakOrRlp.GetOrComputeKeccak();
                SetRootHash(keccak, true);
            }
        }

        private void Commit(NodeRef nodeRef, bool isRoot)
        {
            Node node = nodeRef.Node;
            if (node is Branch branch)
            {
                for (int i = 0; i < 16; i++)
                {
                    NodeRef subnode = branch.Nodes[i];
                    if (subnode?.IsDirty ?? false)
                    {
                        Commit(branch.Nodes[i], false);
                    }
                }
            }
            else if (node is Extension extension)
            {
                if (extension.NextNodeRef.IsDirty)
                {
                    Commit(extension.NextNodeRef, false);
                }
            }

            nodeRef.Node.IsDirty = false;
            nodeRef.ResolveKey();
            if (nodeRef.KeccakOrRlp.IsKeccak || isRoot)
            {
                _db.Set(nodeRef.KeccakOrRlp.GetOrComputeKeccak(), nodeRef.FullRlp.Bytes);
            }
        }

        public void UpdateRootHash()
        {
            RootRef?.ResolveKey();
            SetRootHash(RootRef?.KeccakOrRlp.GetOrComputeKeccak() ?? EmptyTreeHash, false);
        }

        /// <summary>
        ///     0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421
        /// </summary>
        public static readonly Keccak EmptyTreeHash = Keccak.EmptyTreeHash;

        private readonly IDb _db;

        internal NodeRef RootRef;

        internal Node Root
        {
            get
            {
                RootRef?.ResolveNode(this);
                return RootRef?.Node;
            }
        }

        public PatriciaTree()
            : this(new MemDb(), EmptyTreeHash)
        {
        }

        public PatriciaTree(IDb db)
            : this(db, EmptyTreeHash)
        {
        }

        public PatriciaTree(IDb db, Keccak rootHash)
        {
            _db = db;
            RootHash = rootHash;
        }

        private Keccak _rootHash;

        public Keccak RootHash
        {
            get => _rootHash;
            set => SetRootHash(value, true);
        }

        private void SetRootHash(Keccak value, bool resetObjects)
        {
            _rootHash = value;
            if (_rootHash == Keccak.EmptyTreeHash)
            {
                RootRef = null;
            }
            else
            {
                if (resetObjects)
                {
                    RootRef = new NodeRef(new KeccakOrRlp(_rootHash), true);
                }
            }
        }

        private static Rlp RlpEncode(NodeRef nodeRef)
        {
            if (nodeRef == null)
            {
                return Rlp.OfEmptyByteArray;
            }

            nodeRef.ResolveKey();

            return nodeRef.KeccakOrRlp.GetOrEncodeRlp();
        }

        internal static Rlp RlpEncode(Node node)
        {
            Metrics.TreeNodeRlpEncodings++;
            if (node is Leaf leaf)
            {
                Rlp result = Rlp.Encode(Rlp.Encode(leaf.Key.ToBytes()), Rlp.Encode(leaf.Value));
                return result;
            }

            if (node is Branch branch)
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
                    Rlp.Encode(branch.Value ?? new byte[0]));
                return result;
            }

            if (node is Extension extension)
            {
                return Rlp.Encode(
                    Rlp.Encode(extension.Key.ToBytes()),
                    RlpEncode(extension.NextNodeRef));
            }

            throw new InvalidOperationException($"Unknown node type {node?.GetType().Name}");
        }

        internal static Node RlpDecode(Rlp bytes)
        {
            Metrics.TreeNodeRlpDecodings++;
            Rlp.DecoderContext context = bytes.Bytes.AsRlpContext();

            context.ReadSequenceLength();
            int numberOfItems = context.ReadNumberOfItemsRemaining();

            Node result;
            if (numberOfItems == 17)
            {
                NodeRef[] nodes = new NodeRef[16];
                for (int i = 0; i < 16; i++)
                {
                    nodes[i] = DecodeChildNode(context);
                }

                byte[] value = context.DecodeByteArray();
                Branch branch = new Branch(nodes, value);
                result = branch;
            }
            else if (numberOfItems == 2)
            {
                HexPrefix key = HexPrefix.FromBytes(context.DecodeByteArray());
                bool isExtension = key.IsExtension;
                if (isExtension)
                {
                    Extension extension = new Extension(key, DecodeChildNode(context));
                    result = extension;
                }
                else
                {
                    Leaf leaf = new Leaf(key, context.DecodeByteArray());
                    result = leaf;
                }
            }
            else
            {
                throw new InvalidOperationException($"Unexpected number of items = {numberOfItems} when decoding a node");
            }

            return result;
        }

        private static NodeRef DecodeChildNode(Rlp.DecoderContext decoderContext)
        {
            if (decoderContext.IsSequenceNext())
            {
                byte[] sequenceBytes = decoderContext.ReadSequenceRlp();
                if (sequenceBytes.Length >= 32)
                {
                    throw new InvalidOperationException();
                }

                KeccakOrRlp keccakOrRlp = new KeccakOrRlp(new Rlp(sequenceBytes));
                return new NodeRef(keccakOrRlp);
            }

            Keccak keccak = decoderContext.DecodeKeccak();
            return keccak == null ? null : new NodeRef(new KeccakOrRlp(keccak));
        }

        [DebuggerStepThrough]
        public void Set(Nibble[] nibbles, Rlp rlp)
        {
            Set(nibbles, rlp.Bytes);
        }

        [DebuggerStepThrough]
        public virtual void Set(Nibble[] nibbles, byte[] value)
        {
            new TreeOperation(this, nibbles, value, true).Run();
        }

        public byte[] Get(byte[] rawKey)
        {
            return new TreeOperation(this, Nibbles.BytesToNibbleBytes(rawKey), null, false).Run();
        }

        [DebuggerStepThrough]
        public void Set(byte[] rawKey, byte[] value)
        {
            new TreeOperation(this, Nibbles.BytesToNibbleBytes(rawKey), value, true).Run();
        }

        [DebuggerStepThrough]
        public void Set(byte[] rawKey, Rlp value)
        {
            new TreeOperation(this, Nibbles.BytesToNibbleBytes(rawKey), value == null ? new byte[0] : value.Bytes, true).Run();
        }

        internal Node GetNode(KeccakOrRlp keccakOrRlp)
        {
            Rlp rlp = null;
            try
            {
                rlp = new Rlp(keccakOrRlp.IsKeccak ? _db[keccakOrRlp.GetOrComputeKeccak().Bytes] : keccakOrRlp.Bytes);
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }

            return RlpDecode(rlp);
        }

        // TODO: this would only be needed with pruning?
        internal void DeleteNode(KeccakOrRlp hash, bool ignoreChildren = false)
        {
            //            if (hash == null || !hash.IsKeccak)
            //            {
            //                return;
            //            }
            //
            //            Keccak thisNodeKeccak = hash.GetOrComputeKeccak();
            //            Node node = ignoreChildren ? null : RlpDecode(new Rlp(_db[thisNodeKeccak]));
            //            _db.Remove(thisNodeKeccak);
            //
            //            if (ignoreChildren)
            //            {
            //                return;
            //            }
            //
            //            if (node is Extension extension)
            //            {
            //                DeleteNode(extension.NextNodeRef, true);
            //                _db.Remove(hash.GetOrComputeKeccak());
            //            }
            //
            //            if (node is Branch branch)
            //            {
            //                foreach (KeccakOrRlp subnode in branch.Nodes)
            //                {
            //                    DeleteNode(subnode, true);
            //                }
            //            }
        }

        //        internal KeccakOrRlp StoreNode(Node node, bool isRoot = false)
        //        {
        //            if (isRoot && node == null)
        //            {
        ////                DeleteNode(new KeccakOrRlp(RootHash));
        //                RootRef = null;
        ////                _db.Remove(RootHash);
        //                RootHash = EmptyTreeHash;
        //                return new KeccakOrRlp(EmptyTreeHash);
        //            }
        //
        //            if (node == null)
        //            {
        //                return null;
        //            }
        //
        //            Rlp rlp = RlpEncode(node);
        //            KeccakOrRlp key = new KeccakOrRlp(rlp);
        //            if (key.IsKeccak || isRoot)
        //            {
        //                Keccak keyKeccak = key.GetOrComputeKeccak();
        //                _db[keyKeccak.Bytes] = rlp.Bytes;
        //
        //                if (isRoot)
        //                {
        //                    RootRef = node;
        //                    RootHash = keyKeccak;
        //                }
        //            }
        //
        //            return key;
        //        }
    }
}