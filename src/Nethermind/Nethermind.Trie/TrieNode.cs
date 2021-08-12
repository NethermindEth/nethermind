//  Copyright (c) 2021 Demerzel Solutions Limited
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
using Nethermind.Logging;
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
            IsDirty = true;
        }

        public TrieNode(NodeType nodeType, Keccak keccak)
        {
            Keccak = keccak ?? throw new ArgumentNullException(nameof(keccak));
            NodeType = nodeType;
            if (nodeType == NodeType.Unknown)
            {
                IsPersisted = true;
            }
        }

        public TrieNode(NodeType nodeType, byte[] rlp)
        {
            NodeType = nodeType;
            FullRlp = rlp;

            _rlpStream = rlp.AsRlpStream();
        }
        
        public TrieNode(NodeType nodeType, Keccak keccak, byte[] rlp) 
            :this(nodeType, rlp)
        {
            Keccak = keccak;
            if (nodeType == NodeType.Unknown)
            {
                IsPersisted = true;
            }
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
        /// Sealed node is the one that is already immutable except for reference counting and resolving existing data
        /// </summary>
        public bool IsSealed => !IsDirty;

        public bool IsPersisted { get; set; }

        /// <summary>
        /// Node will no longer be mutable
        /// </summary>
        public void Seal()
        {
            if (IsSealed)
            {
                throw new InvalidOperationException($"{nameof(TrieNode)} {this} is already sealed.");
            }

            IsDirty = false;
        }

        public Keccak? Keccak { get; internal set; }

        public byte[]? FullRlp { get; private set; }

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

        public bool IsDirty { get; private set; }

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
                Keccak = null;
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
                    return (byte[])_data![1];
                }

                if (!AllowBranchValues)
                {
                    // branches that we use for state will never have value set as all the keys are equal length
                    return Array.Empty<byte>();
                }

                if (_data![16] is null)
                {
                    if (_rlpStream is null)
                    {
                        _data[16] = Array.Empty<byte>();
                    }
                    else
                    {
                        SeekChild(16);
                        _data![16] = _rlpStream!.DecodeByteArray();
                    }
                }

                return (byte[])_data[16];
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
        public void ResolveNode(ITrieNodeResolver tree)
        {
            try
            {
                if (NodeType == NodeType.Unknown)
                {
                    if (FullRlp is null)
                    {
                        if (Keccak is null)
                        {
                            throw new TrieException("Unable to resolve node without Keccak");
                        }

                        FullRlp = tree.LoadRlp(Keccak);
                        IsPersisted = true;

                        if (FullRlp is null)
                        {
                            throw new TrieException($"Trie returned a NULL RLP for node {Keccak}");
                        }
                    }
                }
                else
                {
                    return;
                }

                _rlpStream = FullRlp.AsRlpStream();
                if (_rlpStream is null)
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

                    // a hack to set internally and still verify attempts from the outside
                    // after the code is ready we should just add proper access control for methods from the outside and inside
                    IsDirty = true;

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

                    IsDirty = false;
                }
                else
                {
                    throw new TrieException($"Unexpected number of items = {numberOfItems} when decoding a node from RLP ({FullRlp?.ToHexString()})");
                }
            }
            catch (RlpException rlpException)
            {
                throw new TrieException($"Error when decoding node {Keccak}", rlpException);
            }
        }
        
        public void ResolveKey(ITrieNodeResolver tree, bool isRoot)
        {
            if (Keccak is not null)
            {
                // please not it is totally fine to leave the RLP null here
                // this node will simply act as a ref only node (a ref to some node with unresolved data in the DB)
                return;
            }

            if (FullRlp is null || IsDirty)
            {
                FullRlp = RlpEncode(tree);
                _rlpStream = FullRlp.AsRlpStream();
            }

            /* nodes that are descendants of other nodes are stored inline
             * if their serialized length is less than Keccak length
             * */
            if (FullRlp.Length >= 32 || isRoot)
            {
                Metrics.TreeNodeHashCalculations++;
                Keccak = Keccak.Compute(FullRlp);
            }
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

        public Keccak? GetChildHash(int i)
        {
            if (_rlpStream is null)
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
                throw new TrieException(
                    "An attempt was made to ask about whether a child is null on a non-branch node.");
            }

            if (_rlpStream is not null && _data?[i] is null)
            {
                SeekChild(i);
                return _rlpStream!.PeekNextRlpLength() == 1;
            }

            return _data?[i] is null || ReferenceEquals(_data[i], _nullNode);
        }

        public long? LastSeen { get; set; }

        public bool IsChildDirty(int i)
        {
            if (IsExtension)
            {
                i++;
            }

            if (_data?[i] is null)
            {
                return false;
            }

            if (ReferenceEquals(_data[i], _nullNode))
            {
                return false;
            }

            if (_data[i] is Keccak)
            {
                return false;
            }

            return ((TrieNode)_data[i])!.IsDirty;
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
            object childOrRef = ResolveChild(tree, childIndex);

            TrieNode? child;
            if (ReferenceEquals(childOrRef, _nullNode) || ReferenceEquals(childOrRef, null))
            {
                child = null;
            }
            else if (childOrRef is TrieNode childNode)
            {
                child = childNode;
            }
            else if (childOrRef is Keccak reference)
            {
                child = tree.FindCachedOrUnknown(reference);
            }
            else
            {
                // we expect this to happen as a Trie traversal error (please see the stack trace above)
                // we need to investigate this case when it happens again
                bool isKeccakCalculated = Keccak is not null && FullRlp is not null;
                bool isKeccakCorrect = isKeccakCalculated && Keccak == Keccak.Compute(FullRlp);
                throw new TrieException($"Unexpected type found at position {childIndex} of {this} with {nameof(_data)} of length {_data?.Length}. Expected a {nameof(TrieNode)} or {nameof(Keccak)} but found {childOrRef?.GetType()} with a value of {childOrRef}. Keccak calculated? : {isKeccakCalculated}; Keccak correct? : {isKeccakCorrect}");
            }

            // pruning trick so we never store long persisted paths
            if (child?.IsPersisted ?? false)
            {
                UnresolveChild(childIndex);
            }

            return child;
        }

        public void ReplaceChildRef(int i, TrieNode child)
        {
            if (child is null)
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
            Keccak = null;
        }

        public long GetMemorySize(bool recursive)
        {
            int keccakSize =
                Keccak is null
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
                if (_data![i] is null)
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
            return
                $"[{NodeType}({FullRlp?.Length}){(FullRlp is not null && FullRlp?.Length < 32 ? $"{FullRlp.ToHexString()}" : "")}" +
                $"|{Id}|{Keccak?.ToShortString()}|{LastSeen}|D:{IsDirty}|S:{IsSealed}|P:{IsPersisted}|";
#else
            return $"[{NodeType}({FullRlp?.Length})|{Keccak?.ToShortString()}|{LastSeen}|D:{IsDirty}|S:{IsSealed}|P:{IsPersisted}|";
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
            TrieNode trieNode = new(NodeType);
            if (_data is not null)
            {
                trieNode.InitData();
                for (int i = 0; i < _data.Length; i++)
                {
                    trieNode._data![i] = _data[i];
                }
            }

            if (FullRlp is not null)
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

        private TrieNode? _storageRoot;

        /// <summary>
        /// Imagine a branch like this:
        ///        B
        /// ||||||||||||||||
        /// -T--TP-K--P--TT-
        /// where T is a transient child (not yet persisted) and P is a persisted child node and K is node hash
        /// After calling this method with <paramref name="skipPersisted"/> == <value>false</value> you will end up with
        ///        B
        /// ||||||||||||||||
        /// -A--AA-K--A--AA-
        /// where A is a <see cref="TrieNode"/> on which the <paramref name="action"/> was invoked.
        /// After calling this method with <paramref name="skipPersisted"/> == <value>true</value> you will end up with
        ///        B
        /// ||||||||||||||||
        /// -A--AP-K--P--AA-
        /// where A is a <see cref="TrieNode"/> on which the <paramref name="action"/> was invoked.
        /// Note that nodes referenced by hash are not called.
        /// </summary>
        public void CallRecursively(
            Action<TrieNode> action,
            ITrieNodeResolver resolver,
            bool skipPersisted,
            ILogger logger)
        {
            if (skipPersisted && IsPersisted)
            {
                if (logger.IsTrace) logger.Trace($"Skipping {this} - already persisted");
                return;
            }

            if (!IsLeaf)
            {
                if (_data is not null)
                {
                    for (int i = 0; i < _data.Length; i++)
                    {
                        object o = _data[i];
                        if (o is TrieNode child)
                        {
                            if (logger.IsTrace) logger.Trace($"Persist recursively on child {i} {child} of {this}");
                            child.CallRecursively(action, resolver, skipPersisted, logger);
                        }
                    }
                }
            }
            else if (TryResolveStorageRoot(resolver))
            {
                if (logger.IsTrace) logger.Trace($"Persist recursively on storage root {_storageRoot} of {this}");
                _storageRoot!.CallRecursively(action, resolver, skipPersisted, logger);
            }

            action(this);
        }

        /// <summary>
        /// Imagine a branch like this:
        ///        B
        /// ||||||||||||||||
        /// -T--TP----P--TT-
        /// where T is a transient child (not yet persisted) and P is a persisted child node
        /// After calling this method you will end up with
        ///        B
        /// ||||||||||||||||
        /// -T--T?----?--TT-
        /// where ? stands for an unresolved child (unresolved child is one for which we know the hash in RLP
        /// and for which we do not have an in-memory .NET object representation - TrieNode)
        /// Unresolved child can be resolved by calling ResolveChild(child_index).
        /// </summary>
        /// <param name="maxLevelsDeep">How many levels deep we will be pruning the child nodes.</param>
        public void PrunePersistedRecursively(int maxLevelsDeep)
        {
            maxLevelsDeep--;
            if (!IsLeaf)
            {
                if (_data is not null)
                {
                    for (int i = 0; i < _data!.Length; i++)
                    {
                        object o = _data[i];
                        if (o is TrieNode child)
                        {
                            if (child.IsPersisted)
                            {
                                Pruning.Metrics.DeepPrunedPersistedNodesCount++;
                                UnresolveChild(i);
                            }
                            else if (maxLevelsDeep != 0)
                            {
                                child.PrunePersistedRecursively(maxLevelsDeep);
                            }
                        }
                    }
                }
            }
            else if (_storageRoot?.IsPersisted ?? false)
            {
                _storageRoot = null;
            }

            // else
            // {
            //     // we assume that the storage root will get resolved during persistence even if not persisted yet
            //     // if this is not true then the code above that is commented out would be critical to call isntead
            //     _storageRoot = null;
            // }
        }

        #region private

        private bool TryResolveStorageRoot(ITrieNodeResolver resolver)
        {
            bool hasStorage = false;
            if (IsLeaf)
            {
                if (_storageRoot is null && (Value?.Length ?? 0) > 64) // if not a storage leaf
                {
                    Keccak storageRoot = _accountDecoder.DecodeStorageRootOnly(Value.AsRlpStream());
                    if (storageRoot != Keccak.EmptyTreeHash)
                    {
                        hasStorage = true;
                        _storageRoot = resolver.FindCachedOrUnknown(storageRoot);
                    }
                }
            }

            return hasStorage;
        }

        private static object _nullNode = new();

        private static TrieNodeDecoder _nodeDecoder = new();

        private static AccountDecoder _accountDecoder = new();

        private static Action<TrieNode> _markPersisted => tn => tn.IsPersisted = true;

        private RlpStream? _rlpStream;

        private object?[]? _data;

        private void InitData()
        {
            if (_data is null)
            {
                switch (NodeType)
                {
                    case NodeType.Unknown:
                        throw new InvalidOperationException(
                            $"Cannot resolve children of an {nameof(NodeType.Unknown)} node");
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
            if (_rlpStream is null)
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

        private object? ResolveChild(ITrieNodeResolver tree, int i)
        {
            object? childOrRef;
            if (_rlpStream is null)
            {
                childOrRef = _data?[i];
            }
            else
            {
                InitData();
                if (_data![i] is null)
                {
                    SeekChild(i);
                    int prefix = _rlpStream!.ReadByte();

                    switch (prefix)
                    {
                        case 0:
                        case 128:
                            _data![i] = childOrRef = _nullNode;
                            break;
                        case 160:
                            _rlpStream.Position--;
                            Keccak keccak = _rlpStream.DecodeKeccak();
                            TrieNode cachedOrUnknown = tree.FindCachedOrUnknown(keccak);
                            _data![i] = childOrRef = cachedOrUnknown;

                            if (IsPersisted && !cachedOrUnknown.IsPersisted)
                            {
                                cachedOrUnknown.CallRecursively(_markPersisted, tree, false, NullLogger.Instance);
                            }

                            break;
                        default:
                        {
                            _rlpStream.Position--;
                            Span<byte> fullRlp = _rlpStream.PeekNextItem();
                            TrieNode child = new(NodeType.Unknown, fullRlp.ToArray());
                            _data![i] = childOrRef = child;
                            break;
                        }
                    }
                }
                else
                {
                    childOrRef = _data?[i];
                }
            }

            return childOrRef;
        }
        
        private void UnresolveChild(int i)
        {
            if (IsPersisted)
            {
                _data![i] = null;
            }
            else
            {
                if (_data![i] is TrieNode childNode)
                {
                    if (!childNode.IsPersisted)
                    {
                        throw new InvalidOperationException("Cannot unresolve a child that is not persisted yet.");
                    }
                    else if (childNode.Keccak != null) // if not by value node
                    {
                        _data![i] = childNode.Keccak;
                    }
                }
            }
        }

        #endregion
    }
}
