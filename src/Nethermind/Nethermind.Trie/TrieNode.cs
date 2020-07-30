//  Copyright (c) 2018 Demerzel Solutions Limited
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
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie.Pruning;

[assembly: InternalsVisibleTo("Ethereum.Trie.Test")]
[assembly: InternalsVisibleTo("Nethermind.Blockchain.Test")]
[assembly: InternalsVisibleTo("Nethermind.Trie.Test")]

namespace Nethermind.Trie
{
    public partial class TrieNode
    {
#if DEBUG
        private static int _idCounter;

        public int Id = Interlocked.Increment(ref _idCounter);
#endif

        public TrieNode(NodeType nodeType)
        {
            NodeType = nodeType;

            // is it equivalent??? maybe can only use IsDirty
            IsDirty = true;
            IsSealed = false;
        }

        public TrieNode(NodeType nodeType, Keccak keccak)
        {
            NodeType = nodeType;
            Keccak = keccak;
            IsSealed = true;
        }

        public TrieNode(NodeType nodeType, byte[] rlp)
        {
            NodeType = nodeType;
            FullRlp = rlp;
            _rlpStream = rlp.AsRlpStream();
            IsSealed = true;
        }

        /// <summary>
        /// Ethereum Patricia Trie specification allows for branch values,
        /// although branched never have values as all the keys are of equal length.
        /// Keys are of length 64 for TxTrie and ReceiptsTrie and StateTrie.
        ///
        /// We leave this switch for testing purposes.
        /// </summary>
        public static bool AllowBranchValues { private get; set; }

        /// <summary>
        /// Temporary reference counting while in memory 
        /// </summary>
        public int Refs
        {
            get => _refs;
            set
            {
                if (IsPersisted)
                {
                    return;
                }
                
                if (value < 0)
                {
                    throw new InvalidOperationException(
                        $"Cannot change {nameof(Refs)} on {this} to {value}");
                }

                _refs = value;
            }
        }

        /// <summary>
        /// Sealed node is the one that is already immutable except for reference counting and resolving existing data
        /// </summary>
        public bool IsSealed
        {
            get => _isSealed;
            private set => _isSealed = value;
        }

        public bool IsPersisted
        {
            get => _isPersisted;
            set => _isPersisted = value;
        }

        /// <summary>
        /// Node will no longer be mutable except for ref counting
        /// </summary>
        public void Seal()
        {
            if (IsSealed)
            {
                throw new InvalidOperationException($"{nameof(TrieNode)} {this} is already sealed.");
            }

            IsDirty = false;
            IsSealed = true;
        }

        public Keccak? Keccak { get; private set; }

        public byte[]? FullRlp
        {
            get => _fullRlp;
            private set
            {
                _fullRlp = value;
                if (_fullRlp != null && _fullRlp.Length < 32)
                {
                    IsPersisted = true;
                    Refs = int.MaxValue;
                }
            }
        }

        public NodeType NodeType { get; private set; }

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
            private set
            {
                if (IsSealed)
                {
                    throw new InvalidOperationException(
                        $"{nameof(TrieNode)} {this} is already sealed when setting {nameof(IsDirty)}.");
                }

                if (value)
                {
                    Keccak = null;
                }

                _isDirty = value;
            }
        }

        public bool IsLeaf => NodeType == NodeType.Leaf;
        public bool IsBranch => NodeType == NodeType.Branch;
        public bool IsExtension => NodeType == NodeType.Extension;

        public byte[]? Path => Key?.Path;

        internal HexPrefix? Key
        {
            get => _data?[0] as HexPrefix;
            set
            {
                if (IsSealed)
                {
                    throw new InvalidOperationException(
                        $"{nameof(TrieNode)} {this} is already sealed when setting {nameof(Key)}.");
                }

                InitData();
                _data![0] = value;
            }
        }

        /// <summary>
        /// Highly optimized
        /// </summary>
        public byte[]? Value
        {
            get
            {
                InitData();
                if (IsLeaf)
                {
                    return (byte[]?) _data![1];
                }

                if (!AllowBranchValues)
                {
                    // branches that we use for state will never have value set as all the keys are equal length
                    return Array.Empty<byte>();
                }

                if (_data![16] == null)
                {
                    if (_rlpStream == null)
                    {
                        _data[16] = Array.Empty<byte>();
                    }
                    else
                    {
                        SeekChild(16);
                        _data![16] = _rlpStream!.DecodeByteArray();
                    }
                }

                return (byte[]) _data[16];
            }

            set
            {
                if (IsSealed)
                {
                    throw new InvalidOperationException(
                        $"{nameof(TrieNode)} {this} is already sealed when setting {nameof(Value)}.");
                }

                InitData();
                if (IsBranch && !AllowBranchValues)
                {
                    // in Ethereum all paths are of equal length, hence branches will never have values
                    // so we decided to save 1/17th of the array size in memory
                    throw new TrieException("Optimized Patricia Trie does not support setting values on branches.");
                }

                _data![IsLeaf ? 1 : 16] = value;
            }
        }

        /// <summary>
        /// Highly optimized
        /// </summary>
        internal void ResolveNode(ITrieNodeResolver tree, bool allowCaching)
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

                        FullRlp = tree.LoadRlp(Keccak, allowCaching);
                        Refs = int.MaxValue;
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

                if (_rlpStream == null)
                {
                    throw new InvalidAsynchronousStateException(
                        $"{nameof(_rlpStream)} is null when {nameof(NodeType)} is {NodeType}");
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

                    IsSealed = false;
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

                    IsSealed = true;
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

        public void ResolveNode(ITrieNodeResolver tree)
        {
            ResolveNode(tree, true);
        }

        public void ResolveKey(ITrieNodeResolver tree, bool isRoot)
        {
            if (Keccak != null)
            {
                return;
            }

            if (FullRlp == null || IsDirty)
            {
                FullRlp = RlpEncode(tree);
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

        internal byte[] RlpEncode(ITrieNodeResolver tree)
        {
            byte[] rlp = _nodeDecoder.Encode(tree, this);
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

        private void ResolveChild(ITrieNodeResolver tree, int i)
        {
            if (_rlpStream == null)
            {
                return;
            }

            InitData();
            if (_data![i] == null)
            {
                SeekChild(i);
                int prefix = _rlpStream!.ReadByte();
                switch (prefix)
                {
                    case 0:
                    case 128:
                        _data![i] = _nullNode;
                        break;
                    case 160:
                        _rlpStream.Position--;
                        Keccak keccak = _rlpStream.DecodeKeccak();
                        _data![i] = tree.FindCachedOrUnknown(keccak);
                        break;
                    default:
                    {
                        _rlpStream.Position--;
                        Span<byte> fullRlp = _rlpStream.PeekNextItem();
                        TrieNode child = new TrieNode(NodeType.Unknown, fullRlp.ToArray());
                        _data![i] = child;
                        break;
                    }
                }
            }
        }

        public Keccak? GetChildHash(int i)
        {
            if (_rlpStream == null)
            {
                return null;
            }

            SeekChild(i);
            (int _, int length) = _rlpStream!.PeekPrefixAndContentLength();
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
                return _rlpStream!.PeekNextRlpLength() == 1;
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

            return ((TrieNode) _data[i])!.IsDirty;
        }

        public TrieNode? this[int i]
        {
            // get => GetChild(i);
            set => SetChild(i, value);
        }

        public TrieNode? GetChild(ITrieNodeResolver tree, int childIndex)
        {
            /* extensions store value before the child while branches store children before the value
             * so just to treat them in the same way we update index on extensions
             */
            childIndex = IsExtension ? childIndex + 1 : childIndex;
            ResolveChild(tree, childIndex);
            return ReferenceEquals(_data![childIndex], _nullNode) ? null : (TrieNode) _data[childIndex];
        }

        public void ReplaceChildRef(int i, TrieNode child)
        {
            if (child == null)
            {
                throw new InvalidOperationException();
            }
            
            InitData();
            int index = IsExtension ? i + 1 : i;
            _data![index] = child;
        }
        
        public void SetChild(int i, TrieNode? node)
        {
            if (IsSealed)
            {
                throw new InvalidOperationException(
                    $"{nameof(TrieNode)} {this} is already sealed when setting a child.");
            }

            InitData();
            int index = IsExtension ? i + 1 : i;
            _data![index] = node ?? _nullNode;
        }

        public long GetMemorySize(bool recursive)
        {
            int keccakSize =
                Keccak == null
                    ? MemorySizes.RefSize
                    : MemorySizes.RefSize + Keccak.MemorySize;
            long fullRlpSize =
                MemorySizes.RefSize +
                (FullRlp is null ? 0 : MemorySizes.Align(FullRlp.Length + MemorySizes.ArrayOverhead));
            long rlpStreamSize =
                MemorySizes.RefSize + (_rlpStream?.MemorySize ?? 0)
                - (FullRlp is null ? 0 : MemorySizes.Align(FullRlp.Length + MemorySizes.ArrayOverhead));
            long dataSize =
                MemorySizes.RefSize +
                (_data is null
                    ? 0
                    : MemorySizes.Align(_data.Length * MemorySizes.RefSize + MemorySizes.ArrayOverhead));
            int objectOverhead = MemorySizes.SmallObjectOverhead - MemorySizes.SmallObjectFreeDataSize;
            int isDirtySize = 1;
            int nodeTypeSize = 1;
            /* _isDirty + NodeType aligned to 4 (is it 8?) and end up in object overhead*/

            for (int i = 0; i < (_data?.Length ?? 0); i++)
            {
                if (_data![i] == null)
                {
                    continue;
                }

                if (_data![i] is Keccak)
                {
                    dataSize += Keccak.MemorySize;
                }

                if (_data![i] is byte[] array)
                {
                    dataSize += MemorySizes.ArrayOverhead + array.Length;
                }

                if (recursive)
                {
                    if (_data![i] is TrieNode node)
                    {
                        dataSize += node.GetMemorySize(true);
                    }
                }
            }

            long unaligned = keccakSize +
                             fullRlpSize +
                             rlpStreamSize +
                             dataSize +
                             isDirtySize +
                             nodeTypeSize +
                             objectOverhead;

            return MemorySizes.Align(unaligned);
        }

        public override string ToString()
        {
#if DEBUG
            return $"[{NodeType}({FullRlp?.Length}){(FullRlp != null && FullRlp?.Length < 32 ? $"{FullRlp.ToHexString()}" : "")}" +
                   $"|{Id}|{Keccak?.ToShortString()}|refs:{Refs}|D:{IsDirty}|S:{IsSealed}|P:{IsPersisted}|";
#else
            return $"[{NodeType}({FullRlp?.Length})|{Keccak?.ToShortString()}|refs:{Refs}|D:{IsDirty}|S:{IsSealed}|P:{IsPersisted}|";
#endif
        }

        public TrieNode CloneWithChangedKey(HexPrefix key)
        {
            TrieNode trieNode = Clone();
            trieNode.Key = key;
            return trieNode;
        }

        public TrieNode Clone()
        {
            TrieNode trieNode = new TrieNode(NodeType);
            if (_data != null)
            {
                trieNode.InitData();
                for (int i = 0; i < _data.Length; i++)
                {
                    trieNode._data![i] = _data[i];
                }
            }

            if (FullRlp != null)
            {
                trieNode.FullRlp = FullRlp;
                trieNode._rlpStream = FullRlp.AsRlpStream();
            }

            return trieNode;
        }

        public TrieNode CloneWithChangedValue(byte[]? changedValue)
        {
            TrieNode trieNode = Clone();
            trieNode.Value = changedValue;
            return trieNode;
        }

        public TrieNode CloneWithChangedKeyAndValue(HexPrefix key, byte[]? changedValue)
        {
            TrieNode trieNode = Clone();
            trieNode.Key = key;
            trieNode.Value = changedValue;
            return trieNode;
        }

        public void DecrementRefsRecursively()
        {
            if (!IsLeaf)
            {
                if (_data != null)
                {
                    for (int i = 0; i < _data.Length; i++)
                    {
                        TrieNode child = _data[i] as TrieNode;
                        ;
                        if (child != null) // both unresolved and NULL are handled here
                        {
                            child.DecrementRefsRecursively();
                            if (child.Refs == 0)
                            {
                                _data[i] = _unresolvedChild;
                            }
                        }
                    }

                    Refs--;
                }
            }
            else
            {
                Refs--;
            }
        }

        public void IncrementRefsRecursively()
        {
            if (!IsLeaf)
            {
                if (_data != null)
                {
                    foreach (object o in _data)
                    {
                        if (o is TrieNode child)
                        {
                            child.IncrementRefsRecursively();
                        }
                    }

                    Refs++;
                }
            }
            else
            {
                Refs++;
            }
        }

        #region private

        private static object _nullNode = new object();

        private static object _unresolvedChild = null!;

        private static TrieNodeDecoder _nodeDecoder = new TrieNodeDecoder();

        private static AccountDecoder _accountDecoder = new AccountDecoder();

        private RlpStream? _rlpStream;

        private object?[]? _data;

        private int _refs;

        private bool _isDirty;

        private bool _isPersisted;

        private bool _isSealed;
        private byte[]? _fullRlp;

        #endregion
    }
}