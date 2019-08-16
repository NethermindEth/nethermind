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
        private static TreeNodeDecoder _nodeDecoder = new TreeNodeDecoder();
        private static AccountDecoder _accountDecoder = new AccountDecoder();

        public static object NullNode = new object();

        internal object[] _data;
        private bool _isDirty;

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
        internal Rlp.DecoderContext DecoderContext { get; set; }
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
                    if (DecoderContext == null)
                    {
                        _data[16] = new byte[0];
                    }
                    else
                    {
                        PositionContextOnItem(16);
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

        private void ResolveNode(PatriciaTree tree, bool allowCaching)
        {
            try
            {
                if (NodeType == NodeType.Unknown)
                {
                    if (FullRlp == null)
                    {
                        FullRlp = tree.GetNode(Keccak, allowCaching);
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


        internal Rlp RlpEncode()
        {
            return _nodeDecoder.Encode(this);
        }

        internal void InitData()
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

        internal void PositionContextOnItem(int itemToSetOn)
        {
            if (DecoderContext == null)
            {
                return;
            }
            
            DecoderContext.Reset();
            DecoderContext.SkipLength();
            for (int i = 0; i < itemToSetOn; i++)
            {
                DecoderContext.SkipItem();
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
                PositionContextOnItem(i);
                int prefix = context.ReadByte();
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

        public Keccak GetChildHash(int i)
        {
            Rlp.DecoderContext context = DecoderContext;
            if (context == null)
            {
                return null;
            }

            if (NodeType == NodeType.Extension)
            {
                context.Reset();
                context.ReadSequenceLength();
                context.DecodeByteArraySpan();

                // TODO: looks like this never supports short extensions? (try the example with the minimal branch)
                return context.DecodeKeccak();
            }

            PositionContextOnItem(i);
            (int _, int length) = context.PeekPrefixAndContentLength();
            return length == 32 ? context.DecodeKeccak() : null;
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
                PositionContextOnItem(i);
                return context.PeekNextRlpLength() == 1;
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

        internal void Accept(ITreeVisitor visitor, PatriciaTree tree, IDb codeDb, VisitContext context)
        {
            try
            {
                ResolveNode(tree, false);
            }
            catch (StateException)
            {
                visitor.VisitMissingNode(Keccak, context);
                return;
            }

            switch (NodeType)
            {
                case NodeType.Unknown:
                    throw new NotImplementedException();
                case NodeType.Branch:
                {
                    visitor.VisitBranch(this, context);
                    context.Level++;
                    for (int i = 0; i < 16; i++)
                    {
                        TrieNode child = GetChild(i);
                        if (child != null && visitor.ShouldVisit(child.Keccak))
                        {
                            context.BranchChildIndex = i;
                            child.Accept(visitor, tree, codeDb, context);
                        }
                    }

                    context.Level--;
                    context.BranchChildIndex = null;
                    break;
                }

                case NodeType.Extension:
                {
                    visitor.VisitExtension(this, context);
                    TrieNode child = GetChild(0);
                    if (child != null && visitor.ShouldVisit(child.Keccak))
                    {
                        context.Level++;
                        context.BranchChildIndex = null;
                        child.Accept(visitor, tree, codeDb, context);
                        context.Level--;
                    }

                    break;
                }

                case NodeType.Leaf:
                {
                    visitor.VisitLeaf(this, context);
                    if (!context.IsStorage)
                    {
                        Account account = _accountDecoder.Decode(Value.AsRlpContext());
                        if (account.HasCode && visitor.ShouldVisit(account.CodeHash))
                        {
                            context.Level++;
                            context.BranchChildIndex = null;
                            visitor.VisitCode(account.CodeHash, codeDb.Get(account.CodeHash), context);
                            context.Level--;
                        }

                        if (account.HasStorage && visitor.ShouldVisit(account.StorageRoot))
                        {
                            context.IsStorage = true;
                            TrieNode storageRoot = new TrieNode(NodeType.Unknown, account.StorageRoot);
                            context.Level++;
                            context.BranchChildIndex = null;
                            storageRoot.Accept(visitor, tree, codeDb, context);
                            context.Level--;
                            context.IsStorage = false;
                        }
                    }

                    break;
                }

                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }
}