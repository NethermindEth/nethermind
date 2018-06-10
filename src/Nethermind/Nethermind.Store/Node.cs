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
using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;

[assembly: InternalsVisibleTo("Ethereum.Trie.Test")]

namespace Nethermind.Store
{
    internal class Node
    {
        public Node(NodeType nodeType)
        {
            NodeType = nodeType;
            if (NodeType == NodeType.Extension)
            {
                Children = new Node[1];
            }
            else if (NodeType == NodeType.Branch)
            {
                Children = new Node[16];
            }
        }

        public Node(NodeType nodeType, Keccak keccak)
        {
            NodeType = nodeType;
            Keccak = keccak;
        }

        public Node(NodeType nodeType, Rlp rlp)
        {
            NodeType = nodeType;
            FullRlp = rlp;
        }

        public Node[] Children { get; set; }

        public bool IsValidWithOneNodeLess
        {
            get
            {
                int nonEmptyNodes = 0;
                for (int i = 0; i < Children.Length; i++)
                {
                    if (Children[i] != null)
                    {
                        nonEmptyNodes++;
                    }
                }

                nonEmptyNodes += (Value?.Length ?? 0) > 0 ? 1 : 0;

                return nonEmptyNodes > 2;
            }
        }

        //public bool IsDirty => Keccak == null && FullRlp.Length >= 32; // possibly?
        public bool IsDirty { get; set; }
        public Keccak Keccak { get; set; }
        public Rlp FullRlp { get; private set; }
        public NodeType NodeType { get; set; }

        public bool IsLeaf => NodeType == NodeType.Leaf;
        public bool IsBranch => NodeType == NodeType.Branch;
        public bool IsExtension => NodeType == NodeType.Extension;

        private static Node DecodeChildNode(Rlp.DecoderContext decoderContext)
        {
            if (decoderContext.IsSequenceNext())
            {
                byte[] sequenceBytes = decoderContext.ReadSequenceRlp();
                if (sequenceBytes.Length >= 32)
                {
                    throw new InvalidOperationException();
                }

                return new Node(NodeType.Unknown, new Rlp(sequenceBytes));
            }

            Keccak keccak = decoderContext.DecodeKeccak();
            return keccak == null ? null : new Node(NodeType.Unknown, keccak);
        }

        public void ResolveNode(PatriciaTree tree)
        {
            if (NodeType == NodeType.Unknown)
            {
                if (FullRlp == null)
                {
                    FullRlp = tree.GetNode(Keccak);
                }
            }
            else
            {
                return;
            }

            Metrics.TreeNodeRlpDecodings++;
            Rlp.DecoderContext context = FullRlp.Bytes.AsRlpContext();

            context.ReadSequenceLength();
            int numberOfItems = context.ReadNumberOfItemsRemaining();

            if (numberOfItems == 17)
            {
                Children = new Node[16];
                for (int i = 0; i < 16; i++)
                {
                    Children[i] = DecodeChildNode(context);
                }

                Value = context.DecodeByteArray();
                NodeType = NodeType.Branch;
            }
            else if (numberOfItems == 2)
            {
                Children = new Node[1];
                HexPrefix key = HexPrefix.FromBytes(context.DecodeByteArray());
                bool isExtension = key.IsExtension;
                if (isExtension)
                {
                    Children[0] = DecodeChildNode(context);
                    Key = key;
                    NodeType = NodeType.Extension;
                }
                else
                {
                    Key = key;
                    Value = context.DecodeByteArray();
                    NodeType = NodeType.Leaf;
                }
            }
            else
            {
                throw new InvalidOperationException($"Unexpected number of items = {numberOfItems} when decoding a node");
            }
        }

        public void ResolveKey(bool isRoot)
        {
            if (Keccak != null)
            {
                return;
            }

            if (FullRlp == null)
            {
                FullRlp = PatriciaTree.RlpEncode(this);
            }
            
            if (FullRlp.Length < 32)
            {
                if (isRoot)
                {
                    Metrics.TreeNodeHashCalculations++;
                    Keccak = Keccak.Compute(FullRlp);
                }

                return;
            }

            Metrics.TreeNodeHashCalculations++;
            Keccak = Keccak.Compute(FullRlp);
        }

        public HexPrefix Key { get; set; }
        public byte[] Value { get; set; }
        public byte[] Path => Key.Path;
    }
}