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
        private bool _isDirty;

        public Node(NodeType nodeType)
        {
            NodeType = nodeType;
            Children = new NodeData(this);
        }

        public Node(NodeType nodeType, Keccak keccak)
        {
            NodeType = nodeType;
            Children = new NodeData(this);
            Keccak = keccak;
        }

        public Node(NodeType nodeType, Rlp rlp)
        {
            NodeType = nodeType;
            Children = new NodeData(this);
            FullRlp = rlp;
        }

        public NodeData Children { get; }

        public bool IsValidWithOneNodeLess
        {
            get
            {
                int nonEmptyNodes = 0;
                for (int i = 0; i < 16; i++)
                {
                    if (!Children.IsChildNull(i)) // TODO: separate null check
                    {
                        nonEmptyNodes++;
                    }
                }

                nonEmptyNodes += (Value?.Length ?? 0) > 0 ? 1 : 0;

                return nonEmptyNodes > 2;
            }
        }

        public bool IsDirty
        {
            get => _isDirty;
            set
            {
                if (value)
                {
                    Keccak = null;
                }

                _isDirty = value;
            }
        }

        public Keccak Keccak { get; set; }
        private Rlp.DecoderContext DecoderContext { get; set; }
        public Rlp FullRlp { get; private set; }
        public NodeType NodeType { get; set; }

        public bool IsLeaf => NodeType == NodeType.Leaf;
        public bool IsBranch => NodeType == NodeType.Branch;
        public bool IsExtension => NodeType == NodeType.Extension;

        public HexPrefix Key
        {
            get => Children.Key;
            set => Children.Key = value;
        }

        public byte[] Value
        {
            get => Children.Value;
            set => Children.Value = value;
        }

        public byte[] Path => Key.Path;

        private static Node DecodeChildNode(Rlp.DecoderContext decoderContext)
        {
            if (decoderContext.IsSequenceNext())
            {
                Span<byte> sequenceBytes = decoderContext.PeekNextItem();
                if (sequenceBytes.Length >= 32)
                {
                    throw new InvalidOperationException();
                }

                return new Node(NodeType.Unknown, new Rlp(sequenceBytes.ToArray()));
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
                    DecoderContext = FullRlp.Bytes.AsRlpContext();
                }
            }
            else
            {
                return;
            }

            Metrics.TreeNodeRlpDecodings++;
            Rlp.DecoderContext context = DecoderContext;
            context.ReadSequenceLength();
            int numberOfItems = context.ReadNumberOfItemsRemaining();

            if (numberOfItems == 17)
            {
                NodeType = NodeType.Branch;
            }
            else if (numberOfItems == 2)
            {
                HexPrefix key = HexPrefix.FromBytes(context.DecodeByteArray());
                bool isExtension = key.IsExtension;
                if (isExtension)
                {
                    NodeType = NodeType.Extension;
                    Children[0] = DecodeChildNode(context);
                    Key = key;
                }
                else
                {
                    NodeType = NodeType.Leaf;
                    Key = key;
                    Value = context.DecodeByteArray();
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

            if (FullRlp == null || IsDirty) // TODO: review
            {
                FullRlp = RlpEncode();
                DecoderContext = FullRlp.Bytes.AsRlpContext();
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

        private Rlp RlpEncodeBranch()
        {
            int valueRlpLength = Rlp.LengthOfByteArray(Value);
            int contentLength = valueRlpLength + Children.GetChildrenRlpLength();
            if (contentLength == 305)
            {

            }
            int sequenceLength = Rlp.GetSequenceRlpLength(contentLength);
            byte[] result = new byte[sequenceLength];
            Span<byte> resultSpan = result.AsSpan();
            int position = Rlp.StartSequence(result, 0, contentLength);
            Children.WriteChildrenRlp(resultSpan.Slice(position, contentLength - valueRlpLength));
            position = sequenceLength - valueRlpLength;
            Rlp.Encode(result, position, Value);
            return new Rlp(result);
        }

        internal Rlp RlpEncode()
        {
            Metrics.TreeNodeRlpEncodings++;
            if (IsLeaf)
            {
                Rlp result = Rlp.Encode(Rlp.Encode(Key.ToBytes()), Rlp.Encode(Value));
                return result;
            }

            if (IsBranch)
            {
                return RlpEncodeBranch();
            }

            if (IsExtension)
            {
                return Rlp.Encode(
                    Rlp.Encode(Key.ToBytes()),
                    RlpEncodeRef(Children[0]));
            }

            throw new InvalidOperationException($"Unknown node type {NodeType}");
        }

        private static Rlp RlpEncodeRef(Node nodeRef)
        {
            if (nodeRef == null)
            {
                return Rlp.OfEmptyByteArray;
            }

            nodeRef.ResolveKey(false);
            return nodeRef.Keccak == null ? nodeRef.FullRlp : Rlp.Encode(nodeRef.Keccak);
        }

        public class NodeData
        {
            private static object _nullNode = new object();

            private readonly Node _parentNode;

            private object[] _data;

            public NodeData(Node parentNode)
            {
                _parentNode = parentNode;
            }

            internal HexPrefix Key
            {
                get => _data[0] as HexPrefix;
                set
                {
                    InitData();
                    _data[0] = value;
                }
            }

            internal byte[] Value
            {
                get
                {
                    InitData();
                    if (_parentNode.IsLeaf)
                    {
                        return (byte[])_data[1];
                    }

                    if (_data[16] == null)
                    {
                        if (_parentNode.DecoderContext == null)
                        {
                            _data[16] = new byte[0];
                        }
                        else
                        {
                            _parentNode.DecoderContext.Position = 0;
                            _parentNode.DecoderContext.SkipLength();
                            for (int i = 0; i < 16; i++)
                            {
                                _parentNode.DecoderContext.SkipItem();
                            }

                            _data[16] = _parentNode.DecoderContext.DecodeByteArray();
                        }
                    }

                    return (byte[])_data[16];
                }

                set
                {
                    InitData();
                    _data[_parentNode.IsLeaf ? 1 : 16] = value;
                }
            }

            public Node this[int i]
            {
                get => GetChild(i);
                set => SetChild(i, value);
            }

            private void InitData()
            {
                if (_data == null)
                {
                    switch (_parentNode.NodeType)
                    {
                        case NodeType.Unknown:
                        throw new InvalidOperationException($"Cannot resolve children of an {nameof(NodeType.Unknown)} node");
                        case NodeType.Branch:
                        _data = new object[17];
                        break;
                        default:
                        _data = new object[2];
                        break;
                    }
                }
            }

            private void ResolveChild(int i)
            {
                Rlp.DecoderContext context = _parentNode.DecoderContext;
                InitData();
                if (context == null)
                {
                    return;
                }

                int index = _parentNode.IsExtension ? i + 1 : i;
                if (_data[index] == null)
                {
                    context.Position = 0;
                    context.SkipLength();
                    for (int _ = 0; _ < index; _++)
                    {
                        context.SkipItem();
                    }

                    int prefix = context.ReadByte();
                    if (prefix == 128)
                    {
                        _data[index] = _nullNode;
                    }
                    else if (prefix == 160)
                    {
                        context.Position--;
                        _data[index] = new Node(NodeType.Unknown, context.DecodeKeccak());
                    }
                    else
                    {
                        context.Position--;
                        //if (context.IsSequenceNext())
                        //{
                        Span<byte> fullRlp = context.PeekNextItem();

                        int sequenceLength = context.ReadSequenceLength();
                        int numberOfItems = context.ReadNumberOfItemsRemaining(context.Position + sequenceLength);

                        Node child;
                        if (numberOfItems == 17)
                        {
                            child = TreeNodeFactory.CreateBranch();
                        }
                        else if (numberOfItems == 2)
                        {
                            HexPrefix key = HexPrefix.FromBytes(context.DecodeByteArray());
                            bool isExtension = key.IsExtension;
                            child = isExtension
                                ? TreeNodeFactory.CreateExtension(key, DecodeChildNode(context))
                                : TreeNodeFactory.CreateLeaf(key, context.DecodeByteArray());
                        }
                        else
                        {
                            throw new InvalidOperationException($"Unexpected number of items = {numberOfItems} when decoding a node");
                        }

                        child.FullRlp = new Rlp(fullRlp.ToArray());
                        _data[index] = child;
                        //}
                    }
                }
            }

            public bool IsChildNull(int i)
            {
                Rlp.DecoderContext context = _parentNode.DecoderContext;
                InitData();
                int index = _parentNode.IsExtension ? i + 1 : i;
                if (context != null)
                {
                    if (_data[index] == null)
                    {
                        context.Position = 0;
                        context.SkipLength();
                        for (int _ = 0; _ < index; _++)
                        {
                            context.SkipItem();
                        }

                        int prefix = context.ReadByte();
                        return prefix == 128;
                    }
                }

                return ReferenceEquals(_data[index], _nullNode) || _data[index] == null;
            }

            public int GetChildrenRlpLength()
            {
                int totalLength = 0;
                Rlp.DecoderContext context = _parentNode.DecoderContext;
                InitData();

                context?.Reset();
                context?.SkipLength();

                for (int i = 0; i < 16; i++)
                {
                    if (context != null && _data[i] == null)
                    {
                        int length = context.PeekNextRlpLength();
                        context.Position += length;
                        totalLength += length;
                    }
                    else
                    {
                        context?.SkipItem();
                        if (ReferenceEquals(_data[i], _nullNode) || _data[i] == null)
                        {
                            totalLength++;
                        }
                        else
                        {
                            Node childNode = (Node)_data[i];
                            childNode.ResolveKey(false);
                            totalLength += childNode.Keccak == null ? childNode.FullRlp.Length : Rlp.LengthOfKeccakRlp;    
                        }
                    }
                }

                return totalLength;
            }

            public void WriteChildrenRlp(Span<byte> destination)
            {
                int position = 0;
                Rlp.DecoderContext context = _parentNode.DecoderContext;
                InitData();

                context?.Reset();
                context?.SkipLength();

                for (int i = 0; i < 16; i++)
                {
                    if (context != null && _data[i] == null)
                    {
                        Span<byte> nextItem = context.PeekNextItem();
                        nextItem.CopyTo(destination.Slice(position, nextItem.Length));
                        position += nextItem.Length;
                    }
                    else
                    {
                        context?.SkipItem();
                        if (ReferenceEquals(_data[i], _nullNode) || _data[i] == null)
                        {
                            destination[position++] = 128;
                        }
                        else
                        {
                            Node childNode = (Node)_data[i];
                            childNode.ResolveKey(false);
                            if (childNode.Keccak == null)
                            {
                                Span<byte> fullRlp = childNode.FullRlp.Bytes.AsSpan();
                                fullRlp.CopyTo(destination.Slice(position, fullRlp.Length));
                                position += fullRlp.Length;
                            }
                            else
                            {
                                position = Rlp.Encode(destination, position, childNode.Keccak.Bytes);
                            }
                        }
                    }
                }
            }

            private Node GetChild(int i)
            {
                int index = _parentNode.IsExtension ? i + 1 : i;
                ResolveChild(i);
                return ReferenceEquals(_data[index], _nullNode) ? null : (Node)_data[index];
            }

            private void SetChild(int i, Node node)
            {
                InitData();
                int index = _parentNode.IsExtension ? i + 1 : i;
                _data[index] = node ?? _nullNode;
            }
        }
    }
}