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
    public enum NodeType
    {
        Unknown,
        Branch,
        Extension,
        Leaf
    }

    internal static class TreeFactory
    {
        public static Node CreateBranch(bool isRoot = false)
        {
            return CreateBranch(new Node[16], new byte[0], isRoot);
        }

        public static Node CreateBranch(Node[] nodes, byte[] value, bool isRoot = false)
        {
            Node node = new Node(NodeType.Branch, isRoot);
            node.Children = nodes;
            node.Value = value;

            if(value == null) throw new ArgumentNullException(nameof(value));
            if(nodes == null) throw new ArgumentNullException(nameof(nodes));

            if (nodes.Length != 16)
            {
                throw new ArgumentException($"{nameof(NodeType.Branch)} should have 16 child nodes", nameof(nodes));
            }

            return node;
        }

        public static Node CreateLeaf(HexPrefix key, byte[] value, bool isRoot = false)
        {
            Node node = new Node(NodeType.Leaf, isRoot);
            node.Key = key;
            node.Value = value;
            return node;
        }

        public static Node CreateExtension(HexPrefix key, bool isRoot = false)
        {
            Node node = new Node(NodeType.Extension, isRoot);
            node.Key = key;
            return node;
        }

        public static Node CreateExtension(HexPrefix key, Node child, bool isRoot = false)
        {
            Node node = new Node(NodeType.Extension, isRoot);
            node.Children[0] = child;
            node.Key = key;
            return node;
        }
    }

    internal class Node
    {
        public Node(NodeType nodeType, bool isRoot)
        {
            NodeType = nodeType;
            IsRoot = isRoot;

            if (NodeType == NodeType.Extension)
            {
                Children = new Node[1];
            }
            else if (NodeType == NodeType.Branch)
            {
                Children = new Node[16];
            }
        }

        public Node(NodeType nodeType, KeccakOrRlp keccakOrRlp, bool isRoot = false)
        {
            NodeType = nodeType;
            KeccakOrRlp = keccakOrRlp;
            IsRoot = isRoot;
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

        public bool IsDirty { get; set; }
        public bool IsRoot { get; set; }
        public KeccakOrRlp KeccakOrRlp { get; set; }
        private Rlp _fullRlp;
        public Rlp FullRlp => _fullRlp;
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

                KeccakOrRlp keccakOrRlp = new KeccakOrRlp(new Rlp(sequenceBytes));
                return new Node(NodeType.Unknown, keccakOrRlp);
            }

            Keccak keccak = decoderContext.DecodeKeccak();
            return keccak == null ? null : new Node(NodeType.Unknown, new KeccakOrRlp(keccak));
        }

        public void ResolveNode(PatriciaTree tree)
        {
            if (NodeType == NodeType.Unknown)
            {
                _fullRlp = tree.GetNode(KeccakOrRlp);
            }
            else
            {
                return;
            }

            Metrics.TreeNodeRlpDecodings++;
            Rlp.DecoderContext context = _fullRlp.Bytes.AsRlpContext();

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

        public void ResolveKey()
        {
            if (KeccakOrRlp == null)
            {
                _fullRlp = PatriciaTree.RlpEncode(this);
                KeccakOrRlp = new KeccakOrRlp(_fullRlp);
            }
        }

        public HexPrefix Key { get; set; }
        public byte[] Value { get; set; }
        public byte[] Path => Key.Path;
    }
}