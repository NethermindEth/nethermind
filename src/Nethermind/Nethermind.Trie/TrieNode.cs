// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie.Pruning;

using static Nethermind.Trie.BranchData;

[assembly: InternalsVisibleTo("Ethereum.Trie.Test")]
[assembly: InternalsVisibleTo("Nethermind.Blockchain.Test")]
[assembly: InternalsVisibleTo("Nethermind.Trie.Test")]

namespace Nethermind.Trie
{
    public sealed partial class TrieNode
    {
        internal const int BranchesCount = 16;
#if DEBUG
        private static int _idCounter;

        public int Id = Interlocked.Increment(ref _idCounter);
#endif

        private static readonly object _nullNode = new();
        private static readonly TrieNodeDecoder _nodeDecoder = new();
        private static readonly AccountDecoder _accountDecoder = new();
        private static readonly Action<TrieNode, Hash256?, TreePath> _markPersisted = static (tn, _, _) => tn.IsPersisted = true;

        private const long _dirtyMask = 0b001;
        private const long _persistedMask = 0b010;
        private const long _boundaryProof = 0b100;
        private const long _flagsMask = 0b111;
        private const long _blockMask = ~_flagsMask;
        private const int _blockShift = 3;

        private long _blockAndFlags = -1L & _blockMask;
        private RlpFactory? _rlp;
        private INodeData? _nodeData;

        /// <summary>
        /// Sealed node is the one that is already immutable except for reference counting and resolving existing data
        /// </summary>
        public bool IsSealed => !IsDirty;

        public long LastSeen
        {
            get => (Volatile.Read(ref _blockAndFlags) >> _blockShift);
            set
            {
                long previousValue = Volatile.Read(ref _blockAndFlags);
                long currentValue;
                do
                {
                    currentValue = previousValue;
                    long newValue = (currentValue & _flagsMask) | (value << _blockShift);
                    previousValue = Interlocked.CompareExchange(ref _blockAndFlags, newValue, currentValue);
                } while (previousValue != currentValue);
            }
        }

        public bool IsPersisted
        {
            get => (Volatile.Read(ref _blockAndFlags) & _persistedMask) != 0;
            set
            {
                long previousValue = Volatile.Read(ref _blockAndFlags);
                long currentValue;
                do
                {
                    currentValue = previousValue;
                    long newValue = value ? (currentValue | _persistedMask) : (currentValue & ~_persistedMask);
                    previousValue = Interlocked.CompareExchange(ref _blockAndFlags, newValue, currentValue);
                } while (previousValue != currentValue);
            }
        }

        public bool IsBoundaryProofNode
        {
            get => (Volatile.Read(ref _blockAndFlags) & _boundaryProof) != 0;
            set
            {
                long previousValue = Volatile.Read(ref _blockAndFlags);
                long currentValue;
                do
                {
                    currentValue = previousValue;
                    long newValue = value ? (currentValue | _boundaryProof) : (currentValue & ~_boundaryProof);
                    previousValue = Interlocked.CompareExchange(ref _blockAndFlags, newValue, currentValue);
                } while (previousValue != currentValue);
            }
        }

        public bool IsDirty => (Volatile.Read(ref _blockAndFlags) & _dirtyMask) != 0;

        /// <summary>
        /// Node will no longer be mutable
        /// </summary>
        public void Seal()
        {
            long previousValue = Volatile.Read(ref _blockAndFlags);
            long currentValue;
            do
            {
                if ((previousValue & _dirtyMask) == 0)
                {
                    ThrowAlreadySealed();
                }
                currentValue = previousValue;
                long newValue = currentValue & ~_dirtyMask;
                previousValue = Interlocked.CompareExchange(ref _blockAndFlags, newValue, currentValue);
            } while (previousValue != currentValue);

            [DoesNotReturn]
            [StackTraceHidden]
            void ThrowAlreadySealed()
            {
                throw new InvalidOperationException($"{nameof(TrieNode)} {this} is already sealed.");
            }
        }

        public Hash256? Keccak { get; internal set; }

        public bool HasRlp => _rlp is not null;

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

        public NodeType NodeType => _nodeData?.NodeType ?? NodeType.Unknown;
        public INodeData? NodeData => _nodeData;

        public bool IsLeaf => NodeType == NodeType.Leaf;
        public bool IsBranch => NodeType == NodeType.Branch;
        public bool IsExtension => NodeType == NodeType.Extension;

        public byte[]? Key
        {
            get => _nodeData is INodeWithKey node ? node?.Key : null;
            internal set
            {
                if (_nodeData is not INodeWithKey node)
                {
                    ThrowDoesNotSupportKey();
                }

                if (IsSealed)
                {
                    if (node.Key.AsSpan().SequenceEqual(value))
                    {
                        // No change, parallel read
                        return;
                    }
                    ThrowAlreadySealed();
                }

                node.Key = value;
                Keccak = null;

                [DoesNotReturn]
                [StackTraceHidden]
                void ThrowDoesNotSupportKey()
                {
                    throw new InvalidOperationException($"{NodeType} {this} is does not support having a {nameof(Key)}.");
                }

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
                if (_nodeData is LeafData data)
                {
                    return ref data.Value;
                }

                // branches that we use for state will never have value set as all the keys are equal length
                return ref CappedArray<byte>.Empty;
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

        private void SetValue(in CappedArray<byte> value)
        {
            if (_nodeData is not LeafData leafData)
            {
                ThrowNoValueOnBranches();
            }

            if (IsSealed)
            {
                ref readonly CappedArray<byte> current = ref leafData.Value;
                if ((current.IsNull && value.IsNull) || (!current.IsNull && !value.IsNull && current.AsSpan().SequenceEqual(value)))
                {
                    // No change, parallel read
                    return;
                }

                ThrowAlreadySealed();
            }

            _nodeData = leafData.CloneWithNewValue(in value);

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

                return nonEmptyNodes > 2;
            }
        }

        private TrieNode(TrieNode node)
        {
            _blockAndFlags |= _dirtyMask;
            _nodeData = node._nodeData?.Clone();
        }

        public TrieNode(NodeType nodeType)
        {
            _blockAndFlags |= _dirtyMask;
            _nodeData = CreateNodeData(nodeType);
        }

        public TrieNode(INodeData nodeData)
        {
            _blockAndFlags |= _dirtyMask;
            _nodeData = nodeData;
        }

        public TrieNode(NodeType nodeType, Hash256 keccak)
        {
            Keccak = keccak ?? throw new ArgumentNullException(nameof(keccak));
            _nodeData = CreateNodeData(nodeType);
            if (nodeType == NodeType.Unknown)
            {
                IsPersisted = true;
            }
        }

        public TrieNode(NodeType nodeType, in CappedArray<byte> rlp, bool isDirty = false)
        {
            if (isDirty)
            {
                _blockAndFlags |= _dirtyMask;
            }
            _nodeData = CreateNodeData(nodeType);

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

        private INodeData CreateNodeData(NodeType nodeType)
            => nodeType switch
            {
                NodeType.Branch => new BranchData(),
                NodeType.Extension => new ExtensionData(),
                NodeType.Leaf => new LeafData(),
                _ => null,
            };

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

        public void ResolveNode(ITrieNodeResolver tree, in TreePath path, ReadFlags readFlags = ReadFlags.None, ICappedArrayPool? bufferPool = null)
        {
            if (NodeType != NodeType.Unknown) return;

            try
            {
                ResolveUnknownNode(tree, path, readFlags, bufferPool);
            }
            catch (RlpException rlpException)
            {
                ThrowDecodingError(rlpException);
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
        internal void ResolveUnknownNode(ITrieNodeResolver tree, in TreePath path, ReadFlags readFlags = ReadFlags.None, ICappedArrayPool? bufferPool = null)
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

            if (numberOfItems < 2)
            {
                return false;
            }
            else if (numberOfItems > 2)
            {
                _nodeData = new BranchData();
            }
            else
            {
                (byte[] key, bool isLeaf) = HexPrefix.FromBytes(rlpStream.DecodeByteArraySpan());
                if (isLeaf)
                {
                    ReadOnlySpan<byte> valueSpan = rlpStream.DecodeByteArraySpan();
                    CappedArray<byte> buffer = bufferPool.SafeRentBuffer(valueSpan.Length);
                    valueSpan.CopyTo(buffer.AsSpan());
                    _nodeData = new LeafData(key, in buffer);
                }
                else
                {
                    _nodeData = new ExtensionData(key);
                }
            }

            return true;
        }

        public void ResolveKey(ITrieNodeResolver tree, ref TreePath path, bool isRoot, ICappedArrayPool? bufferPool = null, bool canBeParallel = true)
        {
            if (Keccak is not null)
            {
                // please note it is totally fine to leave the RLP null here
                // this node will simply act as a ref only node (a ref to some node with unresolved data in the DB)
                return;
            }

            Keccak = GenerateKey(tree, ref path, isRoot, bufferPool, canBeParallel);
        }

        public Hash256? GenerateKey(ITrieNodeResolver tree, ref TreePath path, bool isRoot, ICappedArrayPool? bufferPool = null, bool canBeParallel = true)
        {
            RlpFactory rlp = _rlp;
            if (rlp is null || IsDirty)
            {
                ref readonly CappedArray<byte> oldRlp = ref rlp is not null ? ref rlp.Data : ref CappedArray<byte>.Empty;
                CappedArray<byte> fullRlp = NodeType == NodeType.Branch ?
                    TrieNodeDecoder.RlpEncodeBranch(this, tree, ref path, bufferPool, canBeParallel: isRoot && canBeParallel) :
                    RlpEncode(tree, ref path, bufferPool);

                if (oldRlp.IsNotNullOrEmpty)
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

        internal CappedArray<byte> RlpEncode(ITrieNodeResolver tree, ref TreePath path, ICappedArrayPool? bufferPool = null)
        {
            return NodeType switch
            {
                NodeType.Branch => TrieNodeDecoder.RlpEncodeBranch(this, tree, ref path, bufferPool, canBeParallel: false),
                NodeType.Extension => TrieNodeDecoder.EncodeExtension(this, tree, ref path, bufferPool),
                NodeType.Leaf => TrieNodeDecoder.EncodeLeaf(this, bufferPool),
                _ => ThrowUnhandledNodeType(this)
            };

            [DoesNotReturn]
            [StackTraceHidden]
            static CappedArray<byte> ThrowUnhandledNodeType(TrieNode item)
            {
                throw new TrieException($"An attempt was made to encode a trie node of type {item.NodeType}");
            }
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
            ref var data = ref _nodeData[i];
            if (rlp is not null && data is null)
            {
                ValueRlpStream rlpStream = rlp.GetRlpStream();
                SeekChild(ref rlpStream, i);
                return rlpStream.PeekNextRlpLength() == 1;
            }

            return data is null || ReferenceEquals(data, _nullNode);

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
            ref var data = ref _nodeData[i];
            if (data is null)
            {
                return false;
            }

            if (ReferenceEquals(data, _nullNode))
            {
                return false;
            }

            if (data is Hash256)
            {
                return false;
            }

            return ((TrieNode)data)!.IsDirty;
        }

        public TrieNode? this[int i]
        {
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
                throw new TrieException($"Unexpected type found at position {childIndex} of {this} with {nameof(_nodeData)} of length {_nodeData?.Length}. Expected a {nameof(TrieNode)} or {nameof(Keccak)} but found {childOrRef?.GetType()} with a value of {childOrRef}. Keccak calculated? : {isKeccakCalculated}; Keccak correct? : {isKeccakCorrect}");
            }
        }

        public void ReplaceChildRef(int i, TrieNode child)
        {
            if (child is null)
            {
                throw new InvalidOperationException();
            }

            SetItem(i, child);
        }

        public void SetChild(int i, TrieNode? node)
        {
            if (IsSealed)
            {
                ThrowAlreadySealed();
            }

            SetItem(i, node);
            Keccak = null;

            [DoesNotReturn]
            [StackTraceHidden]
            void ThrowAlreadySealed()
            {
                throw new InvalidOperationException($"{nameof(TrieNode)} {this} is already sealed when setting a child.");
            }
        }

        /// <summary>
        /// Method to avoid expensive Stelem_Ref covariant checks
        /// when setting to object[] array
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SetItem(int i, TrieNode node)
        {
            int index = IsExtension ? i + 1 : i;
            _nodeData[i] = node ?? _nullNode;
        }

        public long GetMemorySize(bool recursive)
        {
            int keccakSize = Keccak is null ? MemorySizes.RefSize : MemorySizes.RefSize + Hash256.MemorySize;
            long rlpSize = MemorySizes.RefSize + (_rlp is null ? 0 : _rlp.MemorySize);
            long dataSize = MemorySizes.RefSize + (_nodeData?.MemorySize ?? 0);
            int objectOverhead = MemorySizes.ObjectHeaderMethodTable;
            int blockAndFlagsSize = sizeof(long);

            if (_nodeData is BranchData data)
            {
                for (int i = 0; i < data.Length; i++)
                {
                    var child = data[i];
                    dataSize += child switch
                    {
                        null => 0,
                        Hash256 => Hash256.MemorySize,
                        byte[] array => MemorySizes.ArrayOverhead + array.Length,
                        CappedArray<byte> cappedArray => MemorySizes.ArrayOverhead + cappedArray.UnderlyingLength + MemorySizes.SmallObjectOverhead,
                        _ => recursive && child is TrieNode node ? node.GetMemorySize(true) : 0
                    };
                }
            }
            else if (_nodeData is ExtensionData extensionData)
            {
                dataSize += extensionData.Value switch
                {
                    null => 0,
                    Hash256 => Hash256.MemorySize,
                    byte[] array => MemorySizes.ArrayOverhead + array.Length,
                    CappedArray<byte> cappedArray => MemorySizes.ArrayOverhead + cappedArray.UnderlyingLength + MemorySizes.SmallObjectOverhead,
                    _ => recursive && extensionData.Value is TrieNode node ? node.GetMemorySize(true) : 0
                };
            }

            long unaligned = keccakSize +
                             rlpSize +
                             dataSize +
                             blockAndFlagsSize +
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
            TrieNode trieNode = new(this);

            RlpFactory rlp = _rlp;
            if (rlp is not null)
            {
                trieNode._rlp = rlp;
            }

            return trieNode;
        }

        [DoesNotReturn]
        [StackTraceHidden]
        static void ThrowIndexOutOfRangeException()
        {
            throw new IndexOutOfRangeException();
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
            int maxPathLength = Int32.MaxValue,
            bool resolveStorageRoot = true)
        {
            if (skipPersisted && IsPersisted)
            {
                if (logger.IsTrace) logger.Trace($"Skipping {this} - already persisted");
                return;
            }

            if (currentPath.Length >= maxPathLength)
            {
                action(this, storageAddress, currentPath);
                return;
            }

            if (_nodeData is BranchData branchData)
            {
                ref readonly var data = ref branchData.Branches;
                int previousLength = AppendChildPath(ref currentPath, 0);
                for (int i = 0; i < BranchArray.Length; i++)
                {
                    if (data[i] is TrieNode child)
                    {
                        if (logger.IsTrace) logger.Trace($"Persist recursively on child {i} {child} of {this}");
                        currentPath.SetLast(i);
                        child.CallRecursively(action, storageAddress, ref currentPath, resolver, skipPersisted, logger, maxPathLength, resolveStorageRoot);
                    }
                }
                currentPath.TruncateMut(previousLength);
            }
            else if (_nodeData is ExtensionData extensionData)
            {
                if (extensionData.Value is TrieNode child)
                {
                    if (logger.IsTrace) logger.Trace($"Persist recursively on child 0 {child} of {this}");
                    int previousLength = AppendChildPath(ref currentPath, 0);
                    child.CallRecursively(action, storageAddress, ref currentPath, resolver, skipPersisted, logger, maxPathLength, resolveStorageRoot);
                    currentPath.TruncateMut(previousLength);
                }
            }
            else if (_nodeData is LeafData leafData)
            {
                TrieNode? storageRoot = leafData.StorageRoot;
                if (storageRoot is not null || (resolveStorageRoot && TryResolveStorageRoot(resolver, ref currentPath, out storageRoot)))
                {
                    if (logger.IsTrace) logger.Trace($"Persist recursively on storage root {leafData.StorageRoot} of {this}");
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

        public ValueTask CallRecursivelyAsync(
            Func<TrieNode, Hash256?, TreePath, ValueTask> action,
            Hash256? storageAddress,
            ref TreePath currentPath,
            ITrieNodeResolver resolver,
            ILogger logger)
        {
            if (IsPersisted)
            {
                if (logger.IsTrace) logger.Trace($"Skipping {this} - already persisted");
                return default;
            }

            if (currentPath.Length >= Int32.MaxValue)
            {
                return action(this, storageAddress, currentPath);
            }

            if (_nodeData is not LeafData leafData)
            {
                if (_nodeData is null)
                {
                    return action(this, storageAddress, currentPath);
                }

                return CallRecursivelyNotLeafAsync(
                    action,
                    storageAddress,
                    currentPath,
                    resolver,
                    logger);
            }
            else
            {
                return CallRecursivelyLeafAsync(
                    action,
                    storageAddress,
                    currentPath,
                    resolver,
                    leafData,
                    logger);
            }
        }

        private async ValueTask CallRecursivelyNotLeafAsync(
            Func<TrieNode, Hash256?, TreePath, ValueTask> action,
            Hash256? storageAddress,
            TreePath currentPath,
            ITrieNodeResolver resolver,
            ILogger logger)
        {
            if (_nodeData is BranchData branchData)
            {
                for (int i = 0; i < BranchArray.Length; i++)
                {
                    if (branchData.Branches[i] is TrieNode child)
                    {
                        if (logger.IsTrace) logger.Trace($"Persist recursively on child {i} {child} of {this}");
                        int previousLength = AppendChildPath(ref currentPath, i);
                        await child.CallRecursivelyAsync(action, storageAddress, ref currentPath, resolver, logger);
                        currentPath.TruncateMut(previousLength);
                    }
                }
            }
            else if (_nodeData is ExtensionData extensionData)
            {
                if (extensionData.Value is TrieNode child)
                {
                    if (logger.IsTrace) logger.Trace($"Persist recursively on child 0 {child} of {this}");
                    int previousLength = AppendChildPath(ref currentPath, 0);
                    await child.CallRecursivelyAsync(action, storageAddress, ref currentPath, resolver, logger);
                    currentPath.TruncateMut(previousLength);
                }
            }

            await action(this, storageAddress, currentPath);
        }

        private async ValueTask CallRecursivelyLeafAsync(
            Func<TrieNode, Hash256?, TreePath, ValueTask> action,
            Hash256? storageAddress,
            TreePath currentPath,
            ITrieNodeResolver resolver,
            LeafData leafData,
            ILogger logger)
        {
            TrieNode? storageRoot = leafData.StorageRoot;
            if (storageRoot is not null || TryResolveStorageRoot(resolver, ref currentPath, out storageRoot))
            {
                if (logger.IsTrace) logger.Trace($"Persist recursively on storage root {storageRoot} of {this}");
                Hash256 storagePathAddr;
                using (currentPath.ScopedAppend(Key))
                {
                    if (currentPath.Length != 64) throw new Exception("unexpected storage path length. Total nibble count should add up to 64.");
                    storagePathAddr = currentPath.Path.ToCommitment();
                }

                TreePath emptyPath = TreePath.Empty;
                await storageRoot!.CallRecursivelyAsync(
                    action,
                    storagePathAddr,
                    ref emptyPath,
                    resolver.GetStorageTrieNodeResolver(storagePathAddr),
                    logger);
            }

            await action(this, storageAddress, currentPath);
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
            if (_nodeData is not LeafData leafData)
            {
                if (_nodeData is BranchData branchData)
                {
                    ref readonly var data = ref branchData.Branches;
                    for (int i = 0; i < BranchArray.Length; i++)
                    {
                        object o = data[i];
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
                else if (_nodeData is ExtensionData extension)
                {
                    if (extension.Value is TrieNode child)
                    {
                        if (child.IsPersisted)
                        {
                            Pruning.Metrics.DeepPrunedPersistedNodesCount++;
                            UnresolveChild(0);
                        }
                        else if (maxLevelsDeep != 0)
                        {
                            child.PrunePersistedRecursively(maxLevelsDeep);
                        }
                    }
                }
            }
            else if (leafData.StorageRoot?.IsPersisted == true)
            {
                leafData.StorageRoot = null;
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

            if (_nodeData is LeafData data)
            {
                storageRoot = data.StorageRoot;
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
                        data.StorageRoot = storageRoot = resolver.GetStorageTrieNodeResolver(storagePath)
                            .FindCachedOrUnknown(in emptyPath, storageRootKey);
                    }
                }
            }
            else
            {
                storageRoot = null;
            }

            return hasStorage;
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
            ref var data = ref _nodeData[i];
            if (rlp is null)
            {
                childOrRef = data;
            }
            else
            {
                if (data is null)
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
                                data = childOrRef = _nullNode;
                                break;
                            }
                        case 160:
                            {
                                rlpStream.Position--;
                                Hash256 keccak = rlpStream.DecodeKeccak();

                                TrieNode child = tree.FindCachedOrUnknown(childPath, keccak);
                                data = childOrRef = child;

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
                                data = childOrRef = child;
                                break;
                            }
                    }
                }
                else
                {
                    childOrRef = data;
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
                path.AppendMut(0);
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

            path.AppendMut(0);
            for (int i = 0; i < 16; i++)
            {
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
            ref var data = ref _nodeData[i];
            if (IsPersisted)
            {
                data = null;
            }
            else
            {
                if (data is TrieNode childNode)
                {
                    if (!childNode.IsPersisted)
                    {
                        ThrowNotPersisted();
                    }
                    else if (childNode.Keccak is not null) // if not by value node
                    {
                        data = childNode.Keccak;
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
