// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
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
        private RlpFactory? _rlp;
        private object?[]? _data;
        private int _isDirty;

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
        public bool IsSealed => _isDirty == 0;

        public bool IsPersisted { get; set; }

        public Hash256? Keccak { get; internal set; }

        public ref readonly CappedArray<byte> FullRlp
        {
            get
            {
                RlpFactory rlp = _rlp;
                return ref rlp is not null ? ref rlp.Data : ref CappedArray<byte>.Null;
            }
        }

        public ValueRlpStream RlpStream
        {
            get
            {
                RlpFactory rlp = _rlp;
                if (rlp is null)
                {
                    return default;
                }
                return rlp.GetRlpStream();
            }
        }

        public NodeType NodeType { get; private set; }

        public bool IsDirty => _isDirty == 1;

        public bool IsLeaf => NodeType == NodeType.Leaf;
        public bool IsBranch => NodeType == NodeType.Branch;
        public bool IsExtension => NodeType == NodeType.Extension;

        public long? LastSeen { get; set; }

        public byte[]? Key
        {
            get => _data?[0] as byte[];
            internal set
            {
                EnsureInitialized();
                if (IsSealed)
                {
                    if ((_data[0] as byte[]).AsSpan().SequenceEqual(value))
                    {
                        // No change, parallel read
                        return;
                    }
                    ThrowAlreadySealed();
                }

                _data![0] = value;
                Keccak = null;

                [DoesNotReturn]
                [StackTraceHidden]
                void ThrowAlreadySealed()
                {
                    throw new InvalidOperationException($"{nameof(TrieNode)} {this} is already sealed when setting {nameof(Key)}.");
                }
            }
        }
        public ref readonly CappedArray<byte> ValueRef
        {
            get
            {
                EnsureInitialized();
                if (IsLeaf)
                {
                    object? data = _data![1];
                    if (data is null)
                    {
                        return ref CappedArray<byte>.Null;
                    }

                    return ref Unsafe.Unbox<CappedArray<byte>>(data);
                }

                if (!AllowBranchValues)
                {
                    // branches that we use for state will never have value set as all the keys are equal length
                    return ref CappedArray<byte>.Empty;
                }

                ref object? obj = ref _data![BranchesCount];
                if (obj is null)
                {
                    RlpFactory rlp = _rlp;
                    if (rlp is null)
                    {
                        obj = CappedArray<byte>.EmptyBoxed;
                        return ref CappedArray<byte>.Empty;
                    }
                    else
                    {
                        ValueRlpStream rlpStream = rlp.GetRlpStream();
                        SeekChild(ref rlpStream, BranchesCount);
                        byte[]? bArr = rlpStream.DecodeByteArray();
                        obj = new CappedArray<byte>(bArr);
                        return ref Unsafe.Unbox<CappedArray<byte>>(obj);
                    }
                }

                return ref Unsafe.Unbox<CappedArray<byte>>(obj);
            }
        }

        /// <summary>
        /// Highly optimized
        /// </summary>
        public CappedArray<byte> Value
        {
            get => ValueRef;
            set => SetValue(in value);
        }

        private void SetValue(in CappedArray<byte> value, bool overrideSealed = false)
        {
            EnsureInitialized();
            var index = IsLeaf ? 1 : BranchesCount;
            ref var data = ref index >= _data.Length ? ref Unsafe.NullRef<object>() : ref _data[index];
            if (!overrideSealed && IsSealed)
            {
                if (data is null)
                {
                    if (!Unsafe.IsNullRef(ref data) && value.IsNull)
                    {
                        // No change, parallel read
                        return;
                    }
                    ThrowAlreadySealed();
                }
                ref readonly var cappedArray = ref Unsafe.Unbox<CappedArray<byte>>(data);
                if ((cappedArray.IsNull && value.IsNull) || (!cappedArray.IsNull && !value.IsNull && cappedArray.AsSpan().SequenceEqual(value)))
                {
                    // No change, parallel read
                    return;
                }

                ThrowAlreadySealed();
            }

            if (IsBranch && !AllowBranchValues)
            {
                // in Ethereum all paths are of equal length, hence branches will never have values
                // so we decided to save 1/17th of the array size in memory
                ThrowNoValueOnBranches();
            }

            if (value.IsNull)
            {
                data = CappedArray<byte>.NullBoxed;
                return;
            }

            data = value;

            [DoesNotReturn]
            [StackTraceHidden]
            void ThrowAlreadySealed()
            {
                throw new InvalidOperationException($"{nameof(TrieNode)} {this} is already sealed when setting {nameof(Value)}.");
            }

            [DoesNotReturn]
            [StackTraceHidden]
            static void ThrowNoValueOnBranches()
            {
                throw new TrieException("Optimized Patricia Trie does not support setting values on branches.");
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
            _isDirty = 1;
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

        public TrieNode(NodeType nodeType, in CappedArray<byte> rlp, bool isDirty = false)
        {
            NodeType = nodeType;
            _isDirty = isDirty ? 1 : 0;

            _rlp = rlp.AsRlpFactory();
        }

        public TrieNode(NodeType nodeType, byte[]? rlp, bool isDirty = false) : this(nodeType, new CappedArray<byte>(rlp), isDirty)
        {
        }

        public TrieNode(NodeType nodeType, Hash256 keccak, ReadOnlySpan<byte> rlp)
            : this(nodeType, keccak, new CappedArray<byte>(rlp.ToArray()))
        {
        }

        public TrieNode(NodeType nodeType, Hash256 keccak, in CappedArray<byte> rlp)
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

            if (Interlocked.Exchange(ref _isDirty, 0) == 0)
            {
                ThrowAlreadySealed();
            }

            [DoesNotReturn]
            [StackTraceHidden]
            void ThrowAlreadySealed()
            {
                throw new InvalidOperationException($"{nameof(TrieNode)} {this} is already sealed.");
            }
        }

        public void ResolveNode(ITrieNodeResolver tree, in TreePath path, ReadFlags readFlags = ReadFlags.None, ICappedArrayPool? bufferPool = null)
        {
            if (NodeType != NodeType.Unknown) return;

            ResolveUnknownNode(tree, path, readFlags, bufferPool);
        }

        /// <summary>
        /// Highly optimized
        /// </summary>
        private void ResolveUnknownNode(ITrieNodeResolver tree, in TreePath path, ReadFlags readFlags = ReadFlags.None, ICappedArrayPool? bufferPool = null)
        {
            try
            {
                RlpFactory rlp = _rlp;
                if (rlp is null)
                {
                    Hash256 keccak = Keccak;
                    if (keccak is null)
                    {
                        ThrowMissingKeccak();
                    }

                    CappedArray<byte> fullRlp = tree.LoadRlp(path, keccak, readFlags);

                    if (fullRlp.IsNull)
                    {
                        ThrowNullRlp();
                    }

                    _rlp = rlp = fullRlp.AsRlpFactory();
                    IsPersisted = true;
                }

                if (!DecodeRlp(rlp.GetRlpStream(), bufferPool, out int numberOfItems))
                {
                    ThrowUnexpectedNumberOfItems(numberOfItems);
                }
            }
            catch (RlpException rlpException)
            {
                ThrowDecodingError(rlpException);
            }

            [DoesNotReturn]
            [StackTraceHidden]
            static void ThrowMissingKeccak()
            {
                throw new TrieException("Unable to resolve node without Keccak");
            }

            [DoesNotReturn]
            [StackTraceHidden]
            void ThrowNullRlp()
            {
                throw new TrieException($"Trie returned a NULL RLP for node {Keccak}");
            }

            [DoesNotReturn]
            [StackTraceHidden]
            void ThrowUnexpectedNumberOfItems(int numberOfItems)
            {
                throw new TrieNodeException($"Unexpected number of items = {numberOfItems} when decoding a node from RLP ({FullRlp.AsSpan().ToHexString()})", Keccak ?? Nethermind.Core.Crypto.Keccak.Zero);
            }

            [DoesNotReturn]
            [StackTraceHidden]
            void ThrowDecodingError(RlpException rlpException)
            {
                throw new TrieNodeException($"Error when decoding node {Keccak}", Keccak ?? Nethermind.Core.Crypto.Keccak.Zero, rlpException);
            }
        }

        /// <summary>
        /// Highly optimized
        /// </summary>
        public bool TryResolveNode(ITrieNodeResolver tree, ref TreePath path, ReadFlags readFlags = ReadFlags.None, ICappedArrayPool? bufferPool = null)
        {
            try
            {
                RlpFactory rplStream = _rlp;
                if (NodeType == NodeType.Unknown)
                {
                    if (rplStream is null)
                    {
                        Hash256 keccak = Keccak;
                        if (keccak is null)
                        {
                            return false;
                        }

                        var fullRlp = tree.TryLoadRlp(path, keccak, readFlags);

                        if (fullRlp is null)
                        {
                            return false;
                        }

                        _rlp = rplStream = fullRlp.AsRlpFactory();
                        IsPersisted = true;
                    }
                }
                else
                {
                    return true;
                }

                return DecodeRlp(rplStream.GetRlpStream(), bufferPool, out _);
            }
            catch (RlpException)
            {
                return false;
            }
        }

        private bool DecodeRlp(ValueRlpStream rlpStream, ICappedArrayPool bufferPool, out int itemsCount)
        {
            Metrics.TreeNodeRlpDecodings++;

            rlpStream.ReadSequenceLength();

            // micro optimization to prevent searches beyond 3 items for branches (search up to three)
            int numberOfItems = itemsCount = rlpStream.PeekNumberOfItemsRemaining(null, 3);

            NodeType nodeType;
            if (numberOfItems < 2)
            {
                return false;
            }
            else if (numberOfItems > 2)
            {
                nodeType = NodeType.Branch;
            }
            else
            {
                (byte[] key, bool isLeaf) = HexPrefix.FromBytes(rlpStream.DecodeByteArraySpan());
                object[] data = [key, null];
                if (isLeaf)
                {
                    nodeType = NodeType.Leaf;

                    ReadOnlySpan<byte> valueSpan = rlpStream.DecodeByteArraySpan();
                    CappedArray<byte> buffer = bufferPool.SafeRentBuffer(valueSpan.Length);
                    valueSpan.CopyTo(buffer.AsSpan());
                    data[1] = buffer.IsNull ? CappedArray<byte>.NullBoxed : buffer;
                    // Overwriting both key and value so just replace the array.
                    Volatile.Write(ref _data, data);
                }
                else
                {
                    nodeType = NodeType.Extension;

                    object[] prev = Interlocked.CompareExchange(ref _data, data, null);
                    // If already set, update the previous array.
                    if (prev is not null) prev[0] = key;
                }
            }

            // Set NodeType after setting key as it alters code path to one that expects the key to be set.
            NodeType = nodeType;
            return true;
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
            RlpFactory rlp = _rlp;
            if (rlp is null || IsDirty)
            {
                ref readonly CappedArray<byte> oldRlp = ref rlp is not null ? ref rlp.Data : ref CappedArray<byte>.Empty;
                CappedArray<byte> fullRlp = RlpEncode(tree, ref path, bufferPool);
                if (fullRlp.IsNotNullOrEmpty)
                {
                    bufferPool.SafeReturnBuffer(oldRlp);
                }
                _rlp = rlp = fullRlp.AsRlpFactory();
            }

            /* nodes that are descendants of other nodes are stored inline
             * if their serialized length is less than Keccak length
             * */
            if (rlp.Data.Length >= 32 || isRoot)
            {
                Metrics.TreeNodeHashCalculations++;
                return Nethermind.Core.Crypto.Keccak.Compute(rlp.Data.AsSpan());
            }

            return null;
        }

        /*
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
        */

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
            RlpFactory rlp = _rlp;
            if (rlp is null)
            {
                return null;
            }

            ValueRlpStream rlpStream = rlp.GetRlpStream();
            SeekChild(ref rlpStream, i);
            (int _, int length) = rlpStream.PeekPrefixAndContentLength();
            return length == 32 ? rlpStream.DecodeKeccak() : null;
        }

        public bool GetChildHashAsValueKeccak(int i, out ValueHash256 keccak)
        {
            Unsafe.SkipInit(out keccak);
            RlpFactory rlp = _rlp;
            if (rlp is null)
            {
                return false;
            }

            ValueRlpStream rlpStream = rlp.GetRlpStream();
            SeekChild(ref rlpStream, i);
            (_, int length) = rlpStream.PeekPrefixAndContentLength();
            if (length == 32 && rlpStream.DecodeValueKeccak(out keccak))
            {
                return true;
            }

            return false;
        }

        public bool IsChildNull(int i)
        {
            if (!IsBranch)
            {
                ThrowNotABranch();
            }

            RlpFactory rlp = _rlp;
            if (rlp is not null && _data?[i] is null)
            {
                ValueRlpStream rlpStream = rlp.GetRlpStream();
                SeekChild(ref rlpStream, i);
                return rlpStream.PeekNextRlpLength() == 1;
            }

            return _data?[i] is null || ReferenceEquals(_data[i], _nullNode);

            [DoesNotReturn]
            [StackTraceHidden]
            static void ThrowNotABranch()
            {
                throw new TrieException("An attempt was made to ask about whether a child is null on a non-branch node.");
            }
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
            TreePath copy = currentPath;
            AppendChildPath(ref copy, childIndex);
            return copy;
        }

        public int AppendChildPath(ref TreePath currentPath, int childIndex)
        {
            int previousLength = currentPath.Length;
            if (IsExtension)
            {
                currentPath.AppendMut(Key);
            }
            else
            {
                currentPath.AppendMut(childIndex);
            }

            return previousLength;
        }

        public void AppendChildPathBranch(ref TreePath currentPath, int childIndex)
        {
            currentPath.AppendMut(childIndex);
        }

        public TrieNode? GetChild(ITrieNodeResolver tree, ref TreePath path, int childIndex)
        {
            int originalLength = path.Length;
            AppendChildPath(ref path, childIndex);
            TrieNode? childNode = GetChildWithChildPath(tree, ref path, childIndex);
            path.TruncateMut(originalLength);
            return childNode;
        }

        public TrieNode? GetChildWithChildPath(ITrieNodeResolver tree, ref TreePath childPath, int childIndex)
        {
            /* extensions store value before the child while branches store children before the value
             * so just to treat them in the same way we update index on extensions
             */
            childIndex = IsExtension ? childIndex + 1 : childIndex;
            object childOrRef = ResolveChildWithChildPath(tree, ref childPath, childIndex);

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
                child = tree.FindCachedOrUnknown(childPath, reference);
            }
            else
            {
                // we expect this to happen as a Trie traversal error (please see the stack trace above)
                // we need to investigate this case when it happens again
                ThrowUnexpectedTypeException(childIndex, childOrRef);
            }

            // pruning trick so we never store long persisted paths
            // Dont unresolve node of path length <= 4. there should be a relatively small number of these, enough to fit
            // in RAM, but they are hit quite a lot, and don't have very good data locality.
            // That said, in practice, it does nothing notable, except for significantly improving benchmark score.
            if (child?.IsPersisted == true && childPath.Length > 4 && childPath.Length % 2 == 0)
            {
                UnresolveChild(childIndex);
            }

            return child;

            [DoesNotReturn]
            [StackTraceHidden]
            void ThrowUnexpectedTypeException(int childIndex, object childOrRef)
            {
                bool isKeccakCalculated = Keccak is not null && FullRlp.IsNotNull;
                bool isKeccakCorrect = isKeccakCalculated && Keccak == Nethermind.Core.Crypto.Keccak.Compute(FullRlp.AsSpan());
                throw new TrieException($"Unexpected type found at position {childIndex} of {this} with {nameof(_data)} of length {_data?.Length}. Expected a {nameof(TrieNode)} or {nameof(Keccak)} but found {childOrRef?.GetType()} with a value of {childOrRef}. Keccak calculated? : {isKeccakCalculated}; Keccak correct? : {isKeccakCorrect}");
            }
        }

        public void ReplaceChildRef(int i, TrieNode child)
        {
            if (child is null)
            {
                throw new InvalidOperationException();
            }

            EnsureInitialized();
            int index = IsExtension ? i + 1 : i;
            _data[index] = child;
        }

        public void SetChild(int i, TrieNode? node)
        {
            if (IsSealed)
            {
                ThrowAlreadySealed();
            }

            EnsureInitialized();
            int index = IsExtension ? i + 1 : i;
            _data[index] = node ?? _nullNode;
            Keccak = null;

            [DoesNotReturn]
            [StackTraceHidden]
            void ThrowAlreadySealed()
            {
                throw new InvalidOperationException($"{nameof(TrieNode)} {this} is already sealed when setting a child.");
            }
        }

        public long GetMemorySize(bool recursive)
        {
            int keccakSize =
                Keccak is null
                    ? MemorySizes.RefSize
                    : MemorySizes.RefSize + Hash256.MemorySize;
            long fullRlpSize =
                MemorySizes.RefSize +
                (FullRlp.IsNull ? 0 : MemorySizes.Align(FullRlp.UnderlyingLength + MemorySizes.ArrayOverhead));
            long rlpStreamSize =
                MemorySizes.RefSize + (_rlp?.MemorySize ?? 0)
                - (FullRlp.IsNull ? 0 : MemorySizes.Align(FullRlp.UnderlyingLength + MemorySizes.ArrayOverhead));
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
                    dataSize += MemorySizes.ArrayOverhead + cappedArray.UnderlyingLength + MemorySizes.SmallObjectOverhead;
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
            object?[] data = _data;
            if (data is not null)
            {
                trieNode.EnsureInitialized();
                for (int i = 0; i < data.Length; i++)
                {
                    trieNode._data[i] = data[i];
                }
            }

            RlpFactory rlp = _rlp;
            if (rlp is not null)
            {
                trieNode._rlp = rlp;
            }

            return trieNode;
        }

        public TrieNode CloneWithChangedValue(in CappedArray<byte> changedValue)
        {
            TrieNode trieNode = Clone();
            trieNode.Value = changedValue;
            return trieNode;
        }

        public TrieNode CloneWithChangedKeyAndValue(byte[] key, in CappedArray<byte> changedValue)
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
            in ILogger logger,
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
                    for (int i = 0; i < _data.Length; i++)
                    {
                        object o = _data[i];
                        if (o is TrieNode child)
                        {
                            if (logger.IsTrace) logger.Trace($"Persist recursively on child {i} {child} of {this}");
                            int previousLength = AppendChildPath(ref currentPath, i);
                            child.CallRecursively(action, storageAddress, ref currentPath, resolver, skipPersisted, logger);
                            currentPath.TruncateMut(previousLength);
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
                    Hash256 storagePathAddr;
                    using (currentPath.ScopedAppend(Key))
                    {
                        if (currentPath.Length != 64) throw new Exception("unexpected storage path length. Total nibble count should add up to 64.");
                        storagePathAddr = currentPath.Path.ToCommitment();
                    }

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
                    Rlp.ValueDecoderContext valueContext = Value.AsSpan().AsRlpValueContext();
                    Hash256 storageRootKey = _accountDecoder.DecodeStorageRootOnly(ref valueContext);
                    if (storageRootKey != Nethermind.Core.Crypto.Keccak.EmptyTreeHash)
                    {
                        Hash256 storagePath;
                        using (currentPath.ScopedAppend(Key))
                        {
                            storagePath = currentPath.Path.ToCommitment();
                        }
                        hasStorage = true;
                        TreePath emptyPath = TreePath.Empty;
                        _storageRoot = storageRoot = resolver.GetStorageTrieNodeResolver(storagePath)
                            .FindCachedOrUnknown(in emptyPath, storageRootKey);
                    }
                }
            }

            return hasStorage;
        }

        [MemberNotNull(nameof(_data))]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnsureInitialized()
        {
            if (_data is null)
            {
                Initialize(NodeType);
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            void Initialize(NodeType nodeType)
            {
                var data = nodeType switch
                {
                    NodeType.Unknown => ThrowCannotResolveException(),
                    NodeType.Branch => new object[AllowBranchValues ? BranchesCount + 1 : BranchesCount],
                    _ => new object[2],
                };

                // Only initialize the array if not already initialized.
                Interlocked.CompareExchange(ref _data, data, null);
                // Set NodeType after setting key as it alters code path to one that expects the key to be set.
                NodeType = nodeType;

                [DoesNotReturn]
                [StackTraceHidden]
                static object[] ThrowCannotResolveException()
                {
                    throw new InvalidOperationException($"Cannot resolve children of an {nameof(NodeType.Unknown)} node");
                }
            }
        }

        private void SeekChild(ref ValueRlpStream rlpStream, int itemToSetOn)
        {
            if (rlpStream.IsNull)
            {
                return;
            }

            SeekChildNotNull(ref rlpStream, itemToSetOn);
        }

        private void SeekChildNotNull(ref ValueRlpStream rlpStream, int itemToSetOn)
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

        private object? ResolveChildWithChildPath(ITrieNodeResolver tree, ref TreePath childPath, int i)
        {
            object? childOrRef;
            RlpFactory rlp = _rlp;
            if (rlp is null)
            {
                childOrRef = _data?[i];
            }
            else
            {
                EnsureInitialized();
                if (_data![i] is null)
                {
                    // Allows to load children in parallel
                    ValueRlpStream rlpStream = rlp.GetRlpStream();
                    SeekChild(ref rlpStream, i);
                    int prefix = rlpStream.ReadByte();

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

                                TrieNode child = tree.FindCachedOrUnknown(childPath, keccak);
                                _data![i] = childOrRef = child;

                                if (IsPersisted && !child.IsPersisted)
                                {
                                    child.CallRecursively(_markPersisted, null, ref childPath, tree, false, NullLogger.Instance);
                                }

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

        /// <summary>
        /// Fast path for trie visitor which visit ranges. Assume node is persisted and has RLP. Does not check for
        /// data[i] and does not modify it as it assume its not in the cache most of the time.
        /// </summary>
        /// <param name="tree"></param>
        /// <param name="path"></param>
        /// <param name="output"></param>
        private void ResolveAllChildBranch(ITrieNodeResolver tree, ref TreePath path, TrieNode?[] output)
        {
            RlpFactory rlp = _rlp;
            if (rlp is null)
            {
                AppendChildPathBranch(ref path, 0);
                for (int i = 0; i < 16; i++)
                {
                    path.SetLast(i);
                    output[i] = GetChildWithChildPath(tree, ref path, i);
                }
                path.TruncateOne();
                return;
            }

            ValueRlpStream rlpStream = rlp.GetRlpStream();
            rlpStream.Reset();
            rlpStream.SkipLength();

            AppendChildPathBranch(ref path, 0);
            for (int i = 0; i < 16; i++)
            {
                // Allows to load children in parallel
                int prefix = rlpStream.PeekByte();

                switch (prefix)
                {
                    case 0:
                    case 128:
                    {
                        rlpStream.Position++;
                        output[i] = null;
                        break;
                    }
                    case 160:
                    {
                        path.SetLast(i);
                        Hash256 keccak = rlpStream.DecodeKeccak();
                        TrieNode child = tree.FindCachedOrUnknown(path, keccak);
                        output[i] = child;

                        break;
                    }
                    default:
                    {
                        Span<byte> fullRlp = rlpStream.PeekNextItem();
                        TrieNode child = new(NodeType.Unknown, fullRlp.ToArray());
                        rlpStream.SkipItem();
                        output[i] = child;
                        break;
                    }
                }
            }
            path.TruncateOne();
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
                        ThrowNotPersisted();
                    }
                    else if (childNode.Keccak is not null) // if not by value node
                    {
                        _data![i] = childNode.Keccak;
                    }
                }
            }

            [DoesNotReturn]
            [StackTraceHidden]
            static void ThrowNotPersisted()
            {
                throw new InvalidOperationException("Cannot unresolve a child that is not persisted yet.");
            }
        }
    }
}
