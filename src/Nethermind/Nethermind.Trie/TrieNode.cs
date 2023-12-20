// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Buffers;
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
        public bool IsBoundaryProofNode { get; set; }

        private TrieNode? _storageRoot;
        private static readonly object _nullNode = new();
        private static readonly TrieNodeDecoder _nodeDecoder = new();
        private static readonly AccountDecoder _accountDecoder = new();
        private static Action<TrieNode, Hash256?, TreePath> _markPersisted => (tn, _, _) => tn.IsPersisted = true;
        private RlpStream? _rlpStream;
        private object?[]? _data;

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

        public Hash256? Keccak { get; internal set; }

        public CappedArray<byte> FullRlp { get; internal set; }

        public NodeType NodeType { get; private set; }

        public bool IsDirty { get; private set; }

        public bool IsLeaf => NodeType == NodeType.Leaf;
        public bool IsBranch => NodeType == NodeType.Branch;
        public bool IsExtension => NodeType == NodeType.Extension;

        public long? LastSeen { get; set; }

        public byte[]? Key
        {
            get => _data?[0] as byte[];
            internal set
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
        public CappedArray<byte> Value
        {
            get
            {
                InitData();
                object? obj;

                if (IsLeaf)
                {
                    obj = _data![1];

                    if (obj is null)
                    {
                        return new CappedArray<byte>(null);
                    }

                    if (obj is byte[] asBytes)
                    {
                        return new CappedArray<byte>(asBytes);
                    }

                    return (CappedArray<byte>)obj;
                }

                if (!AllowBranchValues)
                {
                    // branches that we use for state will never have value set as all the keys are equal length
                    return new CappedArray<byte>(Array.Empty<byte>());
                }

                obj = _data![BranchesCount];
                if (obj is null)
                {
                    if (_rlpStream is null)
                    {
                        _data[BranchesCount] = Array.Empty<byte>();
                        return new CappedArray<byte>(Array.Empty<byte>());
                    }
                    else
                    {
                        SeekChild(BranchesCount);
                        byte[]? bArr = _rlpStream!.DecodeByteArray();
                        _data![BranchesCount] = bArr;
                        return new CappedArray<byte>(bArr);
                    }
                }

                if (obj is byte[] asBytes2)
                {
                    return new CappedArray<byte>(asBytes2);
                }
                return (CappedArray<byte>)obj;
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

                if (value.IsNull)
                {
                    _data![IsLeaf ? 1 : BranchesCount] = null;
                    return;
                }

                if (value.IsUncapped)
                {
                    // Store array directly if possible to reduce memory
                    _data![IsLeaf ? 1 : BranchesCount] = value.Array;
                    return;
                }

                _data![IsLeaf ? 1 : BranchesCount] = value;
            }
        }

        public bool IsValidWithOneNodeLess
        {
            get
            {
                int nonEmptyNodes = 0;
                for (int i = 0; i < BranchesCount; i++)
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
                    nonEmptyNodes += Value.Length > 0 ? 1 : 0;
                }

                return nonEmptyNodes > 2;
            }
        }

        public TrieNode(NodeType nodeType)
        {
            NodeType = nodeType;
            IsDirty = true;
        }

        public TrieNode(NodeType nodeType, Hash256 keccak)
        {
            Keccak = keccak ?? throw new ArgumentNullException(nameof(keccak));
            NodeType = nodeType;
            if (nodeType == NodeType.Unknown)
            {
                IsPersisted = true;
            }
        }

        public TrieNode(NodeType nodeType, CappedArray<byte> rlp, bool isDirty = false)
        {
            NodeType = nodeType;
            FullRlp = rlp;
            IsDirty = isDirty;

            _rlpStream = rlp.AsRlpStream();
        }

        public TrieNode(NodeType nodeType, byte[]? rlp, bool isDirty = false) : this(nodeType, new CappedArray<byte>(rlp), isDirty)
        {
        }

        public TrieNode(NodeType nodeType, Hash256 keccak, ReadOnlySpan<byte> rlp)
            : this(nodeType, keccak, new CappedArray<byte>(rlp.ToArray()))
        {
        }

        public TrieNode(NodeType nodeType, Hash256 keccak, CappedArray<byte> rlp)
            : this(nodeType, rlp)
        {
            Keccak = keccak;
            if (nodeType == NodeType.Unknown)
            {
                IsPersisted = true;
            }
        }

        public override string ToString()
        {
#if DEBUG
            return
                $"[{NodeType}({FullRlp.Length}){(FullRlp.IsNotNull && FullRlp.Length < 32 ? $"{FullRlp.AsSpan().ToHexString()}" : "")}" +
                $"|{Id}|{Keccak}|{LastSeen}|D:{IsDirty}|S:{IsSealed}|P:{IsPersisted}|";
#else
            return $"[{NodeType}({FullRlp.Length})|{Keccak?.ToShortString()}|{LastSeen}|D:{IsDirty}|S:{IsSealed}|P:{IsPersisted}|";
#endif
        }

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

        /// <summary>
        /// Highly optimized
        /// </summary>
        public void ResolveNode(ITrieNodeResolver tree, in TreePath path, ReadFlags readFlags = ReadFlags.None, ICappedArrayPool? bufferPool = null)
        {
            try
            {
                if (NodeType == NodeType.Unknown)
                {
                    if (FullRlp.IsNull)
                    {
                        if (Keccak is null)
                        {
                            throw new TrieException("Unable to resolve node without Keccak");
                        }

                        FullRlp = tree.LoadRlp(path, Keccak, readFlags);
                        IsPersisted = true;

                        if (FullRlp.IsNull)
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
                    throw new InvalidAsynchronousStateException($"{nameof(_rlpStream)} is null when {nameof(NodeType)} is {NodeType}");
                }

                Metrics.TreeNodeRlpDecodings++;
                _rlpStream.ReadSequenceLength();

                // micro optimization to prevent searches beyond 3 items for branches (search up to three)
                int numberOfItems = _rlpStream.PeekNumberOfItemsRemaining(null, 3);

                if (numberOfItems > 2)
                {
                    NodeType = NodeType.Branch;
                }
                else if (numberOfItems == 2)
                {
                    (byte[] key, bool isLeaf) = HexPrefix.FromBytes(_rlpStream.DecodeByteArraySpan());

                    // a hack to set internally and still verify attempts from the outside
                    // after the code is ready we should just add proper access control for methods from the outside and inside
                    bool isDirtyActual = IsDirty;
                    IsDirty = true;

                    if (isLeaf)
                    {
                        NodeType = NodeType.Leaf;
                        Key = key;

                        ReadOnlySpan<byte> valueSpan = _rlpStream.DecodeByteArraySpan();
                        CappedArray<byte> buffer = bufferPool.SafeRentBuffer(valueSpan.Length);
                        valueSpan.CopyTo(buffer.AsSpan());
                        Value = buffer;
                    }
                    else
                    {
                        NodeType = NodeType.Extension;
                        Key = key;
                    }

                    IsDirty = isDirtyActual;
                }
                else
                {
                    throw new TrieNodeException($"Unexpected number of items = {numberOfItems} when decoding a node from RLP ({FullRlp.AsSpan().ToHexString()})", Keccak ?? Nethermind.Core.Crypto.Keccak.Zero);
                }
            }
            catch (RlpException rlpException)
            {
                throw new TrieNodeException($"Error when decoding node {Keccak}", Keccak ?? Nethermind.Core.Crypto.Keccak.Zero, rlpException);
            }
        }

        public void ResolveKey(ITrieNodeResolver tree, ref TreePath path, bool isRoot, ICappedArrayPool? bufferPool = null)
        {
            if (Keccak is not null)
            {
                // please not it is totally fine to leave the RLP null here
                // this node will simply act as a ref only node (a ref to some node with unresolved data in the DB)
                return;
            }

            Keccak = GenerateKey(tree, ref path, isRoot, bufferPool);
        }

        public Hash256? GenerateKey(ITrieNodeResolver tree, ref TreePath path, bool isRoot, ICappedArrayPool? bufferPool = null)
        {
            Hash256? keccak = Keccak;
            if (keccak is not null)
            {
                return keccak;
            }

            if (FullRlp.IsNull || IsDirty)
            {
                CappedArray<byte> oldRlp = FullRlp;
                FullRlp = RlpEncode(tree, ref path, bufferPool);
                if (oldRlp.IsNotNull)
                {
                    bufferPool.SafeReturnBuffer(oldRlp);
                }
                _rlpStream = FullRlp.AsRlpStream();
            }

            /* nodes that are descendants of other nodes are stored inline
             * if their serialized length is less than Keccak length
             * */
            if (FullRlp.Length >= 32 || isRoot)
            {
                Metrics.TreeNodeHashCalculations++;
                return Nethermind.Core.Crypto.Keccak.Compute(FullRlp.AsSpan());
            }

            return null;
        }

        public bool TryResolveStorageRootHash(ITrieNodeResolver resolver, out Hash256? storageRootHash)
        {
            storageRootHash = null;

            if (IsLeaf)
            {
                try
                {
                    storageRootHash = _accountDecoder.DecodeStorageRootOnly(Value.AsRlpStream());
                    if (storageRootHash is not null && storageRootHash != Nethermind.Core.Crypto.Keccak.EmptyTreeHash)
                    {
                        return true;
                    }
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }

        internal CappedArray<byte> RlpEncode(ITrieNodeResolver tree, ref TreePath path, ICappedArrayPool? bufferPool = null)
        {
            CappedArray<byte> rlp = TrieNodeDecoder.Encode(tree, ref path, this, bufferPool);
            // just included here to improve the class reading
            // after some analysis I believe that any non-test Ethereum cases of a trie ever have nodes with RLP shorter than 32 bytes
            // if (rlp.Bytes.Length < 32)
            // {
            //     throw new InvalidDataException("Unexpected less than 32");
            // }

            return rlp;
        }

        public object GetData(int index)
        {
            if (index > _data.Length - 1)
            {
                return null;
            }

            return _data[index];
        }

        public Hash256? GetChildHash(int i)
        {
            if (_rlpStream is null)
            {
                return null;
            }

            SeekChild(i);
            (int _, int length) = _rlpStream!.PeekPrefixAndContentLength();
            return length == 32 ? _rlpStream.DecodeKeccak() : null;
        }

        public bool GetChildHashAsValueKeccak(int i, out ValueHash256 keccak)
        {
            Unsafe.SkipInit(out keccak);
            if (_rlpStream is null)
            {
                return false;
            }

            SeekChild(i);
            (_, int length) = _rlpStream!.PeekPrefixAndContentLength();
            if (length == 32 && _rlpStream.DecodeValueKeccak(out keccak))
            {
                return true;
            }

            return false;
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

            if (_data[i] is Hash256)
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

        public TreePath GetChildPath(in TreePath currentPath, int childIndex)
        {
            if (IsExtension)
            {
                return currentPath.Append(Key);
            }
            else
            {
                return currentPath.Append((byte)childIndex);
            }
        }

        public void GetChildPathMut(ref TreePath currentPath, int childIndex)
        {
            if (IsExtension)
            {
                currentPath.AppendMut(Key);
            }
            else
            {
                currentPath.AppendMut((byte)childIndex);
            }
        }

        public TrieNode? GetChild(ITrieNodeResolver tree, ref TreePath currentPath, int childIndex)
        {
            /* extensions store value before the child while branches store children before the value
             * so just to treat them in the same way we update index on extensions
             */
            childIndex = IsExtension ? childIndex + 1 : childIndex;
            object childOrRef = ResolveChild(tree, ref currentPath, childIndex);

            TrieNode? child;
            if (ReferenceEquals(childOrRef, _nullNode) || childOrRef is null)
            {
                child = null;
            }
            else if (childOrRef is TrieNode childNode)
            {
                child = childNode;
            }
            else if (childOrRef is Hash256 reference)
            {
                int currentPathLength = currentPath.Length;
                GetChildPathMut(ref currentPath, childIndex);
                child = tree.FindCachedOrUnknown(currentPath, reference);
                currentPath.TruncateMut(currentPathLength);
            }
            else
            {
                // we expect this to happen as a Trie traversal error (please see the stack trace above)
                // we need to investigate this case when it happens again
                bool isKeccakCalculated = Keccak is not null && FullRlp.IsNotNull;
                bool isKeccakCorrect = isKeccakCalculated && Keccak == Nethermind.Core.Crypto.Keccak.Compute(FullRlp.AsSpan());
                throw new TrieException($"Unexpected type found at position {childIndex} of {this} with {nameof(_data)} of length {_data?.Length}. Expected a {nameof(TrieNode)} or {nameof(Keccak)} but found {childOrRef?.GetType()} with a value of {childOrRef}. Keccak calculated? : {isKeccakCalculated}; Keccak correct? : {isKeccakCorrect}");
            }

            // pruning trick so we never store long persisted paths
            if (child?.IsPersisted == true)
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
                    : MemorySizes.RefSize + Hash256.MemorySize;
            long fullRlpSize =
                MemorySizes.RefSize +
                (FullRlp.IsNull ? 0 : MemorySizes.Align(FullRlp.Array.Length + MemorySizes.ArrayOverhead));
            long rlpStreamSize =
                MemorySizes.RefSize + (_rlpStream?.MemorySize ?? 0)
                - (FullRlp.IsNull ? 0 : MemorySizes.Align(FullRlp.Array.Length + MemorySizes.ArrayOverhead));
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

                if (_data![i] is Hash256)
                {
                    dataSize += Hash256.MemorySize;
                }

                if (_data![i] is byte[] array)
                {
                    dataSize += MemorySizes.ArrayOverhead + array.Length;
                }

                if (_data![i] is CappedArray<byte> cappedArray)
                {
                    dataSize += MemorySizes.ArrayOverhead + (cappedArray.Array?.Length ?? 0) + MemorySizes.SmallObjectOverhead;
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

        public TrieNode CloneWithChangedKey(byte[] key)
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

            if (FullRlp.IsNotNull)
            {
                trieNode.FullRlp = FullRlp;
                trieNode._rlpStream = FullRlp.AsRlpStream();
            }

            return trieNode;
        }

        public TrieNode CloneWithChangedValue(CappedArray<byte> changedValue)
        {
            TrieNode trieNode = Clone();
            trieNode.Value = changedValue;
            return trieNode;
        }

        public TrieNode CloneWithChangedKeyAndValue(byte[] key, CappedArray<byte> changedValue)
        {
            TrieNode trieNode = Clone();
            trieNode.Key = key;
            trieNode.Value = changedValue;
            return trieNode;
        }

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
            Action<TrieNode, Hash256?, TreePath> action,
            Hash256? storageAddress,
            ref TreePath currentPath,
            ITrieNodeResolver resolver,
            bool skipPersisted,
            ILogger logger,
            bool resolveStorageRoot = true)
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
                    int originalPathLength = currentPath.Length;
                    for (int i = 0; i < _data.Length; i++)
                    {
                        object o = _data[i];
                        if (o is TrieNode child)
                        {
                            if (logger.IsTrace) logger.Trace($"Persist recursively on child {i} {child} of {this}");
                            GetChildPathMut(ref currentPath, i);
                            child.CallRecursively(action, storageAddress, ref currentPath, resolver, skipPersisted, logger);
                            currentPath.TruncateMut(originalPathLength);
                        }
                    }
                }
            }
            else
            {
                TrieNode? storageRoot = _storageRoot;
                if (storageRoot is not null || (resolveStorageRoot && TryResolveStorageRoot(resolver, ref currentPath, out storageRoot)))
                {
                    if (logger.IsTrace) logger.Trace($"Persist recursively on storage root {_storageRoot} of {this}");
                    TreePath storagePath = currentPath.Append(Key);
                    if (storagePath.Length != 64) throw new Exception("unexpected storage path length. Total nibble count should add up to 64.");

                    Hash256 storagePathAddr = storagePath.Path.ToCommitment();
                    TreePath emptyPath = TreePath.Empty;
                    storageRoot!.CallRecursively(
                        action,
                        storagePathAddr,
                        ref emptyPath,
                        resolver.GetStorageTrieNodeResolver(storagePathAddr),
                        skipPersisted,
                        logger);
                }
            }

            action(this, storageAddress, currentPath);
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
            else if (_storageRoot?.IsPersisted == true)
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

        private bool TryResolveStorageRoot(ITrieNodeResolver resolver, ref TreePath currentPath, out TrieNode? storageRoot)
        {
            bool hasStorage = false;
            storageRoot = _storageRoot;

            if (IsLeaf)
            {
                if (storageRoot is not null)
                {
                    hasStorage = true;
                }
                else if (Value.Length > 64) // if not a storage leaf
                {
                    Hash256 storageHash = _accountDecoder.DecodeStorageRootOnly(Value.AsRlpStream());
                    if (storageHash != Nethermind.Core.Crypto.Keccak.EmptyTreeHash)
                    {
                        int originalLength = currentPath.Length;
                        currentPath.AppendMut(Key);
                        hasStorage = true;
                        TreePath emptyPath = TreePath.Empty;
                        _storageRoot = storageRoot = resolver.GetStorageTrieNodeResolver(currentPath.Path.ToCommitment())
                            .FindCachedOrUnknown(in emptyPath, storageHash);

                        currentPath.TruncateMut(originalLength);
                    }
                }
            }

            return hasStorage;
        }

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
                        _data = new object[AllowBranchValues ? BranchesCount + 1 : BranchesCount];
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

            SeekChild(_rlpStream, itemToSetOn);
        }

        private void SeekChild(RlpStream rlpStream, int itemToSetOn)
        {
            rlpStream.Reset();
            rlpStream.SkipLength();
            if (IsExtension)
            {
                rlpStream.SkipItem();
                itemToSetOn--;
            }

            for (int i = 0; i < itemToSetOn; i++)
            {
                rlpStream.SkipItem();
            }
        }

        private object? ResolveChild(ITrieNodeResolver tree, ref TreePath currentPath, int i)
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
                    // Allows to load children in parallel
                    RlpStream rlpStream = new(_rlpStream!.Data!);
                    SeekChild(rlpStream, i);
                    int prefix = rlpStream!.ReadByte();

                    switch (prefix)
                    {
                        case 0:
                        case 128:
                            {
                                _data![i] = childOrRef = _nullNode;
                                break;
                            }
                        case 160:
                            {
                                rlpStream.Position--;
                                Hash256 keccak = rlpStream.DecodeKeccak();

                                int currentPathLength = currentPath.Length;
                                GetChildPathMut(ref currentPath, i);
                                TrieNode child = tree.FindCachedOrUnknown(currentPath, keccak);
                                _data![i] = childOrRef = child;

                                if (IsPersisted && !child.IsPersisted)
                                {
                                    child.CallRecursively(_markPersisted, null, ref currentPath, tree, false, NullLogger.Instance);
                                }

                                currentPath.TruncateMut(currentPathLength);
                                break;
                            }
                        default:
                            {
                                rlpStream.Position--;
                                Span<byte> fullRlp = rlpStream.PeekNextItem();
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
                    else if (childNode.Keccak is not null) // if not by value node
                    {
                        _data![i] = childNode.Keccak;
                    }
                }
            }
        }

        #endregion
    }
}
