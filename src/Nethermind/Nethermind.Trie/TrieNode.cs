﻿//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

[assembly: InternalsVisibleTo("Ethereum.Trie.Test")]
[assembly: InternalsVisibleTo("Nethermind.Blockchain.Test")]

namespace Nethermind.Trie
{
    public class TrieNode
    {
        public static bool AllowBranchValues { private get; set; }
        private static object _nullNode = new object();
        private static TrieNodeDecoder _nodeDecoder = new TrieNodeDecoder();
        private static AccountDecoder _accountDecoder = new AccountDecoder();
        private RlpStream _rlpStream;
        private object[] _data;
        private bool _isDirty;

        /// <summary>
        /// This code is not used in production and has some issues (as pointed out in the article)
        /// It is left here as an incentive to do some more detailed real-time memory analysis of the trie in memory
        /// </summary>
        public int MemorySize
        {
            get
            {
                int unaligned = (Keccak == null ? MemorySizes.RefSize : MemorySizes.RefSize + Keccak.MemorySize) +
                                (MemorySizes.RefSize + FullRlp?.Length ?? MemorySizes.ArrayOverhead) +
                                (MemorySizes.RefSize + _rlpStream?.MemorySize ?? MemorySizes.RefSize) +
                                MemorySizes.RefSize + (MemorySizes.ArrayOverhead + _data?.Length * MemorySizes.RefSize ?? MemorySizes.ArrayOverhead) /* _data */ +
                                MemorySizes.SmallObjectOverhead
                                /* _isDirty + NodeType aligned to 4 (is it 8?) and end up in object overhead*/
                                + (Key?.MemorySize ?? 0);

                return MemorySizes.Align(unaligned);
            }
        }

        public TrieNode(NodeType nodeType)
        {
            NodeType = nodeType;
        }

        public TrieNode(NodeType nodeType, Keccak keccak)
        {
            NodeType = nodeType;
            Keccak = keccak;
        }

        public TrieNode(NodeType nodeType, byte[] rlp)
        {
            NodeType = nodeType;
            FullRlp = rlp;
            _rlpStream = rlp.AsRlpStream();
        }

        public bool IsValidWithOneNodeLess
        {
            get
            {
                int nonEmptyNodes = 0;
                for (int i = 0; i < 16; i++)
                {
                    if (!IsChildNull(i))
                    {
                        nonEmptyNodes++;
                    }

                    if (nonEmptyNodes > 2)
                    {
                        return true;
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
        public byte[] FullRlp { get; private set; }
        public NodeType NodeType { get; private set; }

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

        public byte[] Value
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
                    if (_rlpStream == null)
                    {
                        _data[16] = new byte[0];
                    }
                    else
                    {
                        SeekChild(16);
                        _data[16] = _rlpStream.DecodeByteArray();
                    }
                }

                return (byte[]) _data[16];
            }

            set
            {
                InitData();
                if (IsBranch && !AllowBranchValues)
                {
                    // in Ethereum all paths are of equal length, hence branches will never have values
                    // so we decided to save 1/17th of the array size in memory
                    throw new TrieException("Optimized Patricia Trie does not support setting values on branches.");
                }

                _data[IsLeaf ? 1 : 16] = value;
            }
        }

        public TrieNode this[int i]
        {
            get => GetChild(i);
            set => SetChild(i, value);
        }

        internal void ResolveNode(PatriciaTree tree, bool allowCaching)
        {
            try
            {
                if (NodeType == NodeType.Unknown)
                {
                    if (FullRlp == null)
                    {
                        if (Keccak == null)
                        {
                            throw new TrieException($"Unable to resolve node without Keccak");
                        }

                        FullRlp = tree.GetNode(Keccak, allowCaching);
                        if (FullRlp == null)
                        {
                            throw new TrieException($"Trie returned a malformed RLP for node {Keccak}");
                        }

                        _rlpStream = FullRlp.AsRlpStream();
                    }
                }
                else
                {
                    return;
                }

                Metrics.TreeNodeRlpDecodings++;
                _rlpStream.ReadSequenceLength();

                // micro optimization to prevent searches beyond 3 items for branches (search up to three)
                int numberOfItems = _rlpStream.ReadNumberOfItemsRemaining(null, 3);

                if (numberOfItems > 2)
                {
                    NodeType = NodeType.Branch;
                }
                else if (numberOfItems == 2)
                {
                    HexPrefix key = HexPrefix.FromBytes(_rlpStream.DecodeByteArraySpan());
                    bool isExtension = key.IsExtension;
                    if (isExtension)
                    {
                        NodeType = NodeType.Extension;
                        Key = key;
                    }
                    else
                    {
                        NodeType = NodeType.Leaf;
                        Key = key;
                        Value = _rlpStream.DecodeByteArray();
                    }
                }
                else
                {
                    throw new TrieException($"Unexpected number of items = {numberOfItems} when decoding a node");
                }
            }
            catch (RlpException rlpException)
            {
                throw new TrieException($"Error when decoding node {Keccak}", rlpException);
            }
        }

        public void ResolveNode(PatriciaTree tree)
        {
            ResolveNode(tree, true);
        }

        public void ResolveKey(bool isRoot)
        {
            if (Keccak != null)
            {
                return;
            }

            if (FullRlp == null || IsDirty)
            {
                FullRlp = RlpEncode();
                _rlpStream = FullRlp.AsRlpStream();
            }

            /* nodes that are descendants of other nodes are stored inline
             * if their serialized length is less than Keccak length
             * */
            if (FullRlp.Length < 32 && !isRoot)
            {
                return;
            }

            Metrics.TreeNodeHashCalculations++;
            Keccak = Keccak.Compute(FullRlp);
        }

        internal byte[] RlpEncode()
        {
            byte[] rlp = _nodeDecoder.Encode(this);
            // just included here to improve the class reading
            // after some analysis I believe that any non-test Ethereum cases of a trie ever have nodes with RLP shorter than 32 bytes
            // if (rlp.Bytes.Length < 32)
            // {
            //     throw new InvalidDataException("Unexpected less than 32");
            // }

            return rlp;
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
            }
        }

        private void SeekChild(int itemToSetOn)
        {
            if (_rlpStream == null)
            {
                return;
            }

            _rlpStream.Reset();
            _rlpStream.SkipLength();
            if (IsExtension)
            {
                _rlpStream.SkipItem();
                itemToSetOn--;
            }

            for (int i = 0; i < itemToSetOn; i++)
            {
                _rlpStream.SkipItem();
            }
        }

        private void ResolveChild(int i)
        {
            if (_rlpStream == null)
            {
                return;
            }

            InitData();
            if (_data[i] == null)
            {
                SeekChild(i);
                int prefix = _rlpStream.ReadByte();
                switch (prefix)
                {
                    case 0:
                    case 128:
                        _data[i] = _nullNode;
                        break;
                    case 160:
                        _rlpStream.Position--;
                        _data[i] = new TrieNode(NodeType.Unknown, _rlpStream.DecodeKeccak());
                        break;
                    default:
                    {
                        _rlpStream.Position--;
                        Span<byte> fullRlp = _rlpStream.PeekNextItem();
                        TrieNode child = new TrieNode(NodeType.Unknown, fullRlp.ToArray());
                        _data[i] = child;
                        break;
                    }
                }
            }
        }

        public Keccak GetChildHash(int i)
        {
            if (_rlpStream == null)
            {
                return null;
            }

            SeekChild(i);
            (int _, int length) = _rlpStream.PeekPrefixAndContentLength();
            return length == 32 ? _rlpStream.DecodeKeccak() : null;
        }

        public bool IsChildNull(int i)
        {
            if (!IsBranch)
            {
                throw new TrieException("An attempt was made to ask about whether a child is null on a non-branch node.");
            }

            if (_rlpStream != null && _data?[i] == null)
            {
                SeekChild(i);
                return _rlpStream.PeekNextRlpLength() == 1;
            }

            return _data?[i] == null || ReferenceEquals(_data[i], _nullNode);
        }

        public bool IsChildDirty(int i)
        {
            if (IsExtension)
            {
                i++;
            }

            if (_data?[i] == null)
            {
                return false;
            }

            if (ReferenceEquals(_data[i], _nullNode))
            {
                return false;
            }

            return ((TrieNode) _data[i]).IsDirty;
        }

        public TrieNode GetChild(int childIndex)
        {
            /* extensions store value before the child while branches store children before the value
             * so just to treat them in the same way we update index on extensions
             */
            childIndex = IsExtension ? childIndex + 1 : childIndex;
            ResolveChild(childIndex);
            return ReferenceEquals(_data[childIndex], _nullNode) ? null : (TrieNode) _data[childIndex];
        }

        public void SetChild(int i, TrieNode node)
        {
            InitData();
            int index = IsExtension ? i + 1 : i;
            _data[index] = node ?? _nullNode;
        }

        internal void Accept(ITreeVisitor visitor, PatriciaTree tree, TrieVisitContext trieVisitContext)
        {
            try
            {
                ResolveNode(tree, false);
            }
            catch (TrieException)
            {
                visitor.VisitMissingNode(Keccak, trieVisitContext);
                return;
            }

            switch (NodeType)
            {
                case NodeType.Branch:
                {
                    visitor.VisitBranch(this, trieVisitContext);
                    trieVisitContext.Level++;
                    for (int i = 0; i < 16; i++)
                    {
                        TrieNode child = GetChild(i);
                        if (child != null && visitor.ShouldVisit(child.Keccak))
                        {
                            trieVisitContext.BranchChildIndex = i;
                            child.Accept(visitor, tree, trieVisitContext);
                        }
                    }

                    trieVisitContext.Level--;
                    trieVisitContext.BranchChildIndex = null;
                    break;
                }

                case NodeType.Extension:
                {
                    visitor.VisitExtension(this, trieVisitContext);
                    TrieNode child = GetChild(0);
                    if (child != null && visitor.ShouldVisit(child.Keccak))
                    {
                        trieVisitContext.Level++;
                        trieVisitContext.BranchChildIndex = null;
                        child.Accept(visitor, tree, trieVisitContext);
                        trieVisitContext.Level--;
                    }

                    break;
                }

                case NodeType.Leaf:
                {
                    visitor.VisitLeaf(this, trieVisitContext, Value);
                    if (!trieVisitContext.IsStorage && trieVisitContext.ExpectAccounts) // can combine these conditions
                    {
                        Account account = _accountDecoder.Decode(Value.AsRlpStream());
                        if (account.HasCode && visitor.ShouldVisit(account.CodeHash))
                        {
                            trieVisitContext.Level++;
                            trieVisitContext.BranchChildIndex = null;
                            visitor.VisitCode(account.CodeHash, trieVisitContext);
                            trieVisitContext.Level--;
                        }

                        if (account.HasStorage && visitor.ShouldVisit(account.StorageRoot))
                        {
                            trieVisitContext.IsStorage = true;
                            TrieNode storageRoot = new TrieNode(NodeType.Unknown, account.StorageRoot);
                            trieVisitContext.Level++;
                            trieVisitContext.BranchChildIndex = null;
                            storageRoot.Accept(visitor, tree, trieVisitContext);
                            trieVisitContext.Level--;
                            trieVisitContext.IsStorage = false;
                        }
                    }

                    break;
                }

                default:
                    throw new TrieException($"An attempt was made to visit a node {Keccak} of type {NodeType}");
            }
        }

        private class TrieNodeDecoder
        {
            private byte[] RlpEncodeBranch(TrieNode item)
            {
                int valueRlpLength = AllowBranchValues ? Rlp.LengthOf(item.Value) : 1;
                int contentLength = valueRlpLength + GetChildrenRlpLength(item);
                int sequenceLength = Rlp.GetSequenceRlpLength(contentLength);
                byte[] result = new byte[sequenceLength];
                Span<byte> resultSpan = result.AsSpan();
                int position = Rlp.StartSequence(result, 0, contentLength);
                WriteChildrenRlp(item, resultSpan.Slice(position, contentLength - valueRlpLength));
                position = sequenceLength - valueRlpLength;
                if (AllowBranchValues)
                {
                    Rlp.Encode(result, position, item.Value);
                }
                else
                {
                    result[position] = 128;
                }

                return result;
            }

            public byte[] Encode(TrieNode item)
            {
                Metrics.TreeNodeRlpEncodings++;

                if (item == null)
                {
                    throw new TrieException("An attempt was made to RLP encode a null node.");
                }

                return item.NodeType switch
                {
                    NodeType.Branch => RlpEncodeBranch(item),
                    NodeType.Extension => EncodeExtension(item),
                    NodeType.Leaf => EncodeLeaf(item),
                    _ => throw new TrieException($"An attempt was made to encode a trie node of type {item.NodeType}")
                };
            }

            private static byte[] EncodeExtension(TrieNode item)
            {
                byte[] keyBytes = item.Key.ToBytes();
                TrieNode nodeRef = item.GetChild(0);
                nodeRef.ResolveKey(false);
                int contentLength = Rlp.LengthOf(keyBytes) + (nodeRef.Keccak == null ? nodeRef.FullRlp.Length : Rlp.LengthOfKeccakRlp);
                int totalLength = Rlp.LengthOfSequence(contentLength);
                RlpStream rlpStream = new RlpStream(totalLength);
                rlpStream.StartSequence(contentLength);
                rlpStream.Encode(keyBytes);
                if (nodeRef.Keccak == null)
                {
                    // I think it can only happen if we have a short extension to a branch with a short extension as the only child?
                    // so |
                    // so |
                    // so E - - - - - - - - - - - - - - - 
                    // so |
                    // so |
                    rlpStream.Write(nodeRef.FullRlp);
                }
                else
                {
                    rlpStream.Encode(nodeRef.Keccak);
                }

                return rlpStream.Data;
            }

            private static byte[] EncodeLeaf(TrieNode node)
            {
                if (node.Key == null)
                {
                    throw new TrieException($"Key of a leaf node is null at node {node.Keccak}");
                }

                byte[] keyBytes = node.Key.ToBytes();
                int contentLength = Rlp.LengthOf(keyBytes) + Rlp.LengthOf(node.Value);
                int totalLength = Rlp.LengthOfSequence(contentLength);
                RlpStream rlpStream = new RlpStream(totalLength);
                rlpStream.StartSequence(contentLength);
                rlpStream.Encode(keyBytes);
                rlpStream.Encode(node.Value);
                return rlpStream.Data;
            }

            private int GetChildrenRlpLength(TrieNode item)
            {
                int totalLength = 0;
                item.InitData();
                item.SeekChild(0);
                for (int i = 0; i < 16; i++)
                {
                    if (item._rlpStream != null && item._data[i] == null)
                    {
                        (int prefixLength, int contentLength) = item._rlpStream.PeekPrefixAndContentLength();
                        totalLength += prefixLength + contentLength;
                    }
                    else
                    {
                        if (ReferenceEquals(item._data[i], _nullNode) || item._data[i] == null)
                        {
                            totalLength++;
                        }
                        else
                        {
                            TrieNode childNode = (TrieNode) item._data[i];
                            childNode.ResolveKey(false);
                            totalLength += childNode.Keccak == null ? childNode.FullRlp.Length : Rlp.LengthOfKeccakRlp;
                        }
                    }

                    item._rlpStream?.SkipItem();
                }

                return totalLength;
            }

            private void WriteChildrenRlp(TrieNode item, Span<byte> destination)
            {
                int position = 0;
                var rlpStream = item._rlpStream;
                item.InitData();
                item.SeekChild(0);
                for (int i = 0; i < 16; i++)
                {
                    if (rlpStream != null && item._data[i] == null)
                    {
                        int length = rlpStream.PeekNextRlpLength();
                        Span<byte> nextItem = rlpStream.Data.AsSpan().Slice(rlpStream.Position, length);
                        nextItem.CopyTo(destination.Slice(position, nextItem.Length));
                        position += nextItem.Length;
                        rlpStream.SkipItem();
                    }
                    else
                    {
                        rlpStream?.SkipItem();
                        if (ReferenceEquals(item._data[i], _nullNode) || item._data[i] == null)
                        {
                            destination[position++] = 128;
                        }
                        else
                        {
                            TrieNode childNode = (TrieNode) item._data[i];
                            childNode.ResolveKey(false);
                            if (childNode.Keccak == null)
                            {
                                Span<byte> fullRlp = childNode.FullRlp.AsSpan();
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
        }
    }
}