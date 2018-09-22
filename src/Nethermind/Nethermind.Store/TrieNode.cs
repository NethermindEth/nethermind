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
    internal class TrieNode
    {
        private static readonly object NullNode = new object();

        private object[] _data;
        private bool _isDirty;

        private short[] _lookupTable;

        public TrieNode(NodeType nodeType)
        {
            NodeType = nodeType;
        }

        public TrieNode(NodeType nodeType, Keccak keccak)
        {
            NodeType = nodeType;
            Keccak = keccak;
        }

        public TrieNode(NodeType nodeType, Rlp rlp)
        {
            NodeType = nodeType;
            FullRlp = rlp;
            DecoderContext = rlp.Bytes.AsRlpContext();
            BuildLookupTable();
        }

        public bool IsValidWithOneNodeLess
        {
            get
            {
                int nonEmptyNodes = 0;
                for (int i = 0; i < 16; i++)
                {
                    if (!IsChildNull(i)) // TODO: separate null check
                    {
                        nonEmptyNodes++;
                    }
                }

                if (AllowBranchValues)
                {
                    nonEmptyNodes += (Value?.Length ?? 0) > 0 ? 1 : 0;
                }

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

        public byte[] Path => Key.Path;

        internal HexPrefix Key
        {
            get => _data[0] as HexPrefix;
            set
            {
                InitData();
                _data[0] = value;
            }
        }

        public static bool AllowBranchValues { get; set; } = false;

        internal byte[] Value
        {
            get
            {
                InitData();
                if (IsLeaf)
                {
                    return (byte[]) _data[1];
                }

                if (!AllowBranchValues)
                {
                    // branches that we use for state will never have value set as all the keys are equal length
                    return new byte[0];
                }

                if (_data[16] == null)
                {
                    if (DecoderContext == null)
                    {
                        _data[16] = new byte[0];
                    }
                    else
                    {
                        DecoderContext.Position = _lookupTable[32];
                        _data[16] = DecoderContext.DecodeByteArray();
                    }
                }

                return (byte[]) _data[16];
            }

            set
            {
                InitData();
                _data[IsLeaf ? 1 : 16] = value;
            }
        }

        public TrieNode this[int i]
        {
            get => GetChild(i);
            set => SetChild(i, value);
        }

        private static TrieNode DecodeChildNode(Rlp.DecoderContext decoderContext)
        {
            if (decoderContext.IsSequenceNext())
            {
                Span<byte> sequenceBytes = decoderContext.PeekNextItem();
                if (sequenceBytes.Length >= 32)
                {
                    throw new InvalidOperationException();
                }

                return new TrieNode(NodeType.Unknown, new Rlp(sequenceBytes.ToArray()));
            }

            Keccak keccak = decoderContext.DecodeKeccak();
            return keccak == null ? null : new TrieNode(NodeType.Unknown, keccak);
        }

        public void ResolveNode(PatriciaTree tree)
        {
            try
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
                        SetChild(0, DecodeChildNode(context));
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
            catch (Exception e)
            {
                throw new StateException($"Unable to resolve node {Keccak.ToString(true)}", e);
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
                BuildLookupTable();
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
            int contentLength = valueRlpLength + GetChildrenRlpLength();
            int sequenceLength = Rlp.GetSequenceRlpLength(contentLength);
            byte[] result = new byte[sequenceLength];
            Span<byte> resultSpan = result.AsSpan();
            int position = Rlp.StartSequence(result, 0, contentLength);
            WriteChildrenRlp(resultSpan.Slice(position, contentLength - valueRlpLength));
            position = sequenceLength - valueRlpLength;
            Rlp.Encode(result, position, Value);
            return new Rlp(result);
        }

        internal Rlp RlpEncode()
        {
            Metrics.TreeNodeRlpEncodings++;
            if (IsLeaf)
            {
                return Rlp.Encode(Rlp.Encode(Key.ToBytes()), Rlp.Encode(Value));
            }

            if (IsBranch)
            {
                return RlpEncodeBranch();
            }

            if (IsExtension)
            {
                return Rlp.Encode(Rlp.Encode(Key.ToBytes()), RlpEncodeRef(GetChild(0)));
            }

            throw new InvalidOperationException($"Unknown node type {NodeType}");
        }

        private static Rlp RlpEncodeRef(TrieNode nodeRef)
        {
            if (nodeRef == null)
            {
                return Rlp.OfEmptyByteArray;
            }

            nodeRef.ResolveKey(false);
            return nodeRef.Keccak == null ? nodeRef.FullRlp : Rlp.Encode(nodeRef.Keccak);
        }

        private void InitData()
        {
            if (_data == null)
            {
                switch (NodeType)
                {
                    case NodeType.Unknown:
                        throw new InvalidOperationException($"Cannot resolve children of an {nameof(NodeType.Unknown)} node");
                    case NodeType.Branch:
                        _data = new object[AllowBranchValues ? 17 : 16];
                        break;
                    default:
                        _data = new object[2];
                        break;
                }

                if (_lookupTable == null)
                {
                    BuildLookupTable();
                }
            }
        }

        public void BuildLookupTable()
        {
            if (IsBranch && DecoderContext != null)
            {
                if (_lookupTable == null)
                {
                    _lookupTable = new short[34];
                }
                else
                {
                    Array.Clear(_lookupTable, 0, _lookupTable.Length);
                }

                DecoderContext.Reset();
                DecoderContext.SkipLength();
                short offset = (short) DecoderContext.Position;
                for (int i = 0; i < 17; i++)
                {
                    short nextLength = (short) DecoderContext.PeekNextRlpLength();
                    _lookupTable[i * 2] = offset;
                    _lookupTable[i * 2 + 1] = nextLength;
                    offset += nextLength;
                    DecoderContext.Position += nextLength;
                }
            }
        }

        private void ResolveChild(int i)
        {
            Rlp.DecoderContext context = DecoderContext;
            InitData();
            if (context == null)
            {
                return;
            }

            if (_data[i] == null)
            {
                context.Position = _lookupTable[i * 2];
                int prefix = context.ReadByte();
                if (prefix == 128)
                {
                    _data[i] = NullNode;
                }
                else if (prefix == 160)
                {
                    context.Position--;
                    _data[i] = new TrieNode(NodeType.Unknown, context.DecodeKeccak());
                }
                else
                {
                    context.Position--;
                    Span<byte> fullRlp = context.PeekNextItem();
                    TrieNode child = new TrieNode(NodeType.Unknown, new Rlp(fullRlp.ToArray()));
                    _data[i] = child;
                }
            }
        }

        public bool IsChildNull(int i)
        {
            Rlp.DecoderContext context = DecoderContext;
            InitData();
            if (!IsBranch)
            {
                throw new InvalidOperationException("only on branch");
            }

            if (context != null && _data[i] == null)
            {
                return _lookupTable[i * 2 + 1] == 1;
            }

            return ReferenceEquals(_data[i], NullNode) || _data[i] == null;
        }

        public bool IsChildDirty(int i)
        {
            if (_data?[i] == null)
            {
                return false;
            }

            if (ReferenceEquals(_data[i], NullNode))
            {
                return false;
            }

            return ((TrieNode) _data[i]).IsDirty;
        }

        public int GetChildrenRlpLength()
        {
            int totalLength = 0;
            InitData();

            for (int i = 0; i < 16; i++)
            {
                if (DecoderContext != null && _data[i] == null)
                {
                    totalLength += _lookupTable[i * 2 + 1];
                }
                else
                {
                    if (ReferenceEquals(_data[i], NullNode) || _data[i] == null)
                    {
                        totalLength++;
                    }
                    else
                    {
                        TrieNode childNode = (TrieNode) _data[i];
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
            Rlp.DecoderContext context = DecoderContext;
            InitData();

            for (int i = 0; i < 16; i++)
            {
                if (context != null && _data[i] == null)
                {
                    context.Position = _lookupTable[i * 2];
                    Span<byte> nextItem = context.Read(_lookupTable[i * 2 + 1]);
                    nextItem.CopyTo(destination.Slice(position, nextItem.Length));
                    position += nextItem.Length;
                }
                else
                {
                    if (ReferenceEquals(_data[i], NullNode) || _data[i] == null)
                    {
                        destination[position++] = 128;
                    }
                    else
                    {
                        TrieNode childNode = (TrieNode) _data[i];
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

        public TrieNode GetChild(int i)
        {
            int index = IsExtension ? i + 1 : i;
            ResolveChild(i);
            return ReferenceEquals(_data[index], NullNode) ? null : (TrieNode) _data[index];
        }

        public void SetChild(int i, TrieNode node)
        {
            InitData();
            int index = IsExtension ? i + 1 : i;
            _data[index] = node ?? NullNode;
        }
    }
}