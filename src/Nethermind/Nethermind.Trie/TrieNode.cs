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
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

[assembly: InternalsVisibleTo("Ethereum.Trie.Test")]
[assembly: InternalsVisibleTo("Nethermind.Blockchain.Test")]
[assembly: InternalsVisibleTo("Nethermind.Trie.Test")]

namespace Nethermind.Trie
{
    public partial class TrieNode
    {
        /// <summary>
        /// Ethereum Patricia Trie specification allows for branch values,
        /// although branched never have values as all the keys are of equal length.
        /// Keys are of length 64 for TxTrie and ReceiptsTrie and StateTrie.
        ///
        /// We leave this switch for testing purposes.
        /// </summary>
        public static bool AllowBranchValues { private get; set; }
        
        private static object _nullNode = new object();
        
        private static TrieNodeDecoder _nodeDecoder = new TrieNodeDecoder();
        
        private static AccountDecoder _accountDecoder = new AccountDecoder();
        
        private RlpStream _rlpStream;
        
        private object[] _data;
        
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

        public TrieNode(NodeType nodeType, byte[] rlp)
        {
            NodeType = nodeType;
            FullRlp = rlp;
            _rlpStream = rlp.AsRlpStream();
        }
        
        public Keccak Keccak { get; set; }
        
        public byte[] FullRlp { get; private set; }
        
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
            set
            {
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

        public byte[] Path => Key.Path;

        internal HexPrefix Key
        {
            get => _data?[0] as HexPrefix;
            set
            {
                InitData();
                _data[0] = value;
            }
        }

        /// <summary>
        /// Highly optimized
        /// </summary>
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
                    return Array.Empty<byte>();
                }

                if (_data[16] == null)
                {
                    if (_rlpStream == null)
                    {
                        _data[16] = Array.Empty<byte>();
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

        /// <summary>
        /// Highly optimized
        /// </summary>
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
        
        public TrieNode this[int i]
        {
            get => GetChild(i);
            set => SetChild(i, value);
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

        public int MemorySize
        {
            get
            {
                int keccakSize =
                    Keccak == null
                        ? MemorySizes.RefSize
                        : MemorySizes.RefSize + Keccak.MemorySize;
                int fullRlpSize =
                    MemorySizes.RefSize +
                    (FullRlp is null ? 0 : MemorySizes.Align(FullRlp.Length + MemorySizes.ArrayOverhead));
                int rlpStreamSize =
                    MemorySizes.RefSize + (_rlpStream?.MemorySize ?? 0)
                    - (FullRlp is null ? 0 : MemorySizes.Align(FullRlp.Length + MemorySizes.ArrayOverhead));
                int dataSize =
                    MemorySizes.RefSize +
                    (_data is null
                        ? 0
                        : MemorySizes.Align(_data.Length * MemorySizes.RefSize + MemorySizes.ArrayOverhead));
                int objectOverhead = MemorySizes.SmallObjectOverhead - MemorySizes.SmallObjectFreeDataSize;
                int isDirtySize = 1;
                int nodeTypeSize = 1;
                /* _isDirty + NodeType aligned to 4 (is it 8?) and end up in object overhead*/

                int unaligned = keccakSize +
                                fullRlpSize +
                                rlpStreamSize +
                                dataSize +
                                isDirtySize +
                                nodeTypeSize +
                                objectOverhead;

                return MemorySizes.Align(unaligned);
            }
        }
    }
}