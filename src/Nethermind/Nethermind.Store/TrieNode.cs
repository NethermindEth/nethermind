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
using System.Text;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;

[assembly: InternalsVisibleTo("Ethereum.Trie.Test")]

namespace Nethermind.Store
{
    public class TrieNode
    {
        public static bool AllowBranchValues { private get; set; }
        
        private static TrieNodeDecoder _nodeDecoder = new TrieNodeDecoder();
        private static AccountDecoder _accountDecoder = new AccountDecoder();
        private RlpStream _rlpStream;
        private object[] _data;
        private bool _isDirty;

        private static object NullNode = new object();

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
            _rlpStream = rlp.Bytes.AsRlpStream();
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
                        FullRlp = tree.GetNode(Keccak, allowCaching);
                        _rlpStream = FullRlp.Bytes.AsRlpStream();
                    }
                }
                else
                {
                    return;
                }

                Metrics.TreeNodeRlpDecodings++;
                _rlpStream.ReadSequenceLength();
                int numberOfItems = _rlpStream.ReadNumberOfItemsRemaining();

                if (numberOfItems == 17)
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
                    throw new InvalidOperationException($"Unexpected number of items = {numberOfItems} when decoding a node");
                }
            }
            catch (Exception e)
            {
                throw new StateException($"Unable to resolve node {Keccak.ToString(true)}", e);
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
                _rlpStream = FullRlp.Bytes.AsRlpStream();
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


        internal Rlp RlpEncode()
        {
            return _nodeDecoder.Encode(this);
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
                if (prefix == 0)
                {
                    _data[i] = NullNode;
                }
                else if (prefix == 128)
                {
                    _data[i] = NullNode;
                }
                else if (prefix == 160)
                {
                    _rlpStream.Position--;
                    _data[i] = new TrieNode(NodeType.Unknown, _rlpStream.DecodeKeccak());
                }
                else
                {
                    _rlpStream.Position--;
                    Span<byte> fullRlp = _rlpStream.PeekNextItem();
                    TrieNode child = new TrieNode(NodeType.Unknown, new Rlp(fullRlp.ToArray()));
                    _data[i] = child;
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
                throw new InvalidOperationException("only on branch");
            }

            if (_rlpStream != null && _data?[i] == null)
            {
                SeekChild(i);
                return _rlpStream.PeekNextRlpLength() == 1;
            }

            return ReferenceEquals(_data[i], NullNode) || _data?[i] == null;
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

        public TrieNode GetChild(int childIndex)
        {
            /* extensions store value before the child while branches store children before the value
             * so just to treat them in the same way we update index on extensions
             */
            childIndex = IsExtension ? childIndex + 1 : childIndex;
            ResolveChild(childIndex);
            return ReferenceEquals(_data[childIndex], NullNode) ? null : (TrieNode) _data[childIndex];
        }

        public void SetChild(int i, TrieNode node)
        {
            InitData();
            int index = IsExtension ? i + 1 : i;
            _data[index] = node ?? NullNode;
        }

        internal void Accept(ITreeVisitor visitor, PatriciaTree tree, IDb codeDb, VisitContext visitContext)
        {
            try
            {
                ResolveNode(tree, false);
            }
            catch (StateException)
            {
                visitor.VisitMissingNode(Keccak, visitContext);
                return;
            }

            switch (NodeType)
            {
                case NodeType.Unknown:
                    throw new NotImplementedException();
                case NodeType.Branch:
                {
                    visitor.VisitBranch(this, visitContext);
                    visitContext.Level++;
                    for (int i = 0; i < 16; i++)
                    {
                        TrieNode child = GetChild(i);
                        if (child != null && visitor.ShouldVisit(child.Keccak))
                        {
                            visitContext.BranchChildIndex = i;
                            child.Accept(visitor, tree, codeDb, visitContext);
                        }
                    }

                    visitContext.Level--;
                    visitContext.BranchChildIndex = null;
                    break;
                }

                case NodeType.Extension:
                {
                    visitor.VisitExtension(this, visitContext);
                    TrieNode child = GetChild(0);
                    if (child != null && visitor.ShouldVisit(child.Keccak))
                    {
                        visitContext.Level++;
                        visitContext.BranchChildIndex = null;
                        child.Accept(visitor, tree, codeDb, visitContext);
                        visitContext.Level--;
                    }

                    break;
                }

                case NodeType.Leaf:
                {
                    visitor.VisitLeaf(this, visitContext, Value);
                    if (!visitContext.IsStorage)
                    {
                        Account account = _accountDecoder.Decode(Value.AsRlpStream());
                        if (account.HasCode && visitor.ShouldVisit(account.CodeHash))
                        {
                            visitContext.Level++;
                            visitContext.BranchChildIndex = null;
                            visitor.VisitCode(account.CodeHash, codeDb.Get(account.CodeHash), visitContext);
                            visitContext.Level--;
                        }

                        if (account.HasStorage && visitor.ShouldVisit(account.StorageRoot))
                        {
                            visitContext.IsStorage = true;
                            TrieNode storageRoot = new TrieNode(NodeType.Unknown, account.StorageRoot);
                            visitContext.Level++;
                            visitContext.BranchChildIndex = null;
                            storageRoot.Accept(visitor, tree, codeDb, visitContext);
                            visitContext.Level--;
                            visitContext.IsStorage = false;
                        }
                    }

                    break;
                }

                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private class TrieNodeDecoder
        {
            private Rlp RlpEncodeBranch(TrieNode item)
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
                
                return new Rlp(result);
            }

            public Rlp Encode(TrieNode item)
            {
                Metrics.TreeNodeRlpEncodings++;
                if (item == null)
                {
                    return Rlp.OfEmptySequence;
                }

                if (item.IsLeaf)
                {
                    return EncodeLeaf(item);
                }

                if (item.IsBranch)
                {
                    return RlpEncodeBranch(item);
                }

                if (item.IsExtension)
                {
                    return EncodeExtension(item);
                }

                throw new InvalidOperationException($"Unknown node type {item.NodeType}");
            }

            private static Rlp EncodeExtension(TrieNode item)
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
                    rlpStream.Encode(nodeRef.FullRlp);
                }
                else
                {
                    rlpStream.Encode(nodeRef.Keccak);
                }

                return new Rlp(rlpStream.Data);
            }

            private static Rlp EncodeLeaf(TrieNode item)
            {
                byte[] keyBytes = item.Key.ToBytes();
                int contentLength = Rlp.LengthOf(keyBytes) + Rlp.LengthOf(item.Value);
                int totalLength = Rlp.LengthOfSequence(contentLength);
                RlpStream rlpStream = new RlpStream(totalLength);
                rlpStream.StartSequence(contentLength);
                rlpStream.Encode(keyBytes);
                rlpStream.Encode(item.Value);
                return new Rlp(rlpStream.Data);
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
                        if (ReferenceEquals(item._data[i], TrieNode.NullNode) || item._data[i] == null)
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
                        if (ReferenceEquals(item._data[i], TrieNode.NullNode) || item._data[i] == null)
                        {
                            destination[position++] = 128;
                        }
                        else
                        {
                            TrieNode childNode = (TrieNode) item._data[i];
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
        }
    }
}