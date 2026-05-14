// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Threading;
using System.Threading.Tasks;
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
    [StructLayout(LayoutKind.Sequential)]
    public abstract partial class TrieNode
    {
        internal const int BranchesCount = 16;
#if DEBUG
        private static int _idCounter;

        public int Id = Interlocked.Increment(ref _idCounter);
#endif

        // Empty-child sentinel: a typed TrieNode published into branch / extension
        // slots when the parent RLP encoded an empty entry. Compared by reference;
        // never resolved or visited. See TrieNodeNullSentinel.
        public static readonly TrieNode NullNode = new TrieNodeNullSentinel();
        private static readonly AccountDecoder _accountDecoder = new();

        private const byte _dirtyMask = 0b0001;
        private const byte _persistedMask = 0b0010;
        private const byte _boundaryProof = 0b0100;
        private const byte _hasKeccakMask = 0b1000;

        private byte _blockAndFlags = 0;
        // Seqlock counter for torn-read safety on _keccakValue (32 bytes, not atomically
        // readable on x64). Writers set bit 0 to indicate in-progress, advance by 2 on
        // completion. Readers retry on observed odd value or before/after mismatch.
        // Fits in alignment padding after _blockAndFlags so adds no object size.
        private uint _keccakSeq;
        // Seqlock for torn-read safety: CappedArray<byte> is 12 bytes (ref + int),
        // not atomically readable on x64. Split into two 8-byte fields that are
        // individually atomic, with a sequence counter to detect concurrent writes.
        private byte[]? _rlpArray;
        private ulong _rlpSeqAndLength; // normal: bits 0-31 length, 32-63 sequence. slice: bit 63, bits 0-31 length, bits 32-62 offset.
        // Inline node hash. Access only via TryGetKeccak / KeccakValue / HasKeccak; raw
        // reads are not torn-safe under concurrent writes. After B4 the shape data
        // (branches / key / value / storage root) moved onto the typed derived classes,
        // so cache-line 1 of TrieNode now holds only the hot flag / RLP fields plus
        // this 32-byte hash that eliminates the separate Hash256 object the pre-A1
        // design allocated.
        private ValueHash256 _keccakValue;

        // In normal mode, the sequence counter shares bit 63 with the slice flag. After about
        // 2^30 completed writes to one node, doneSeq reaches 0x80000000 and IsRlpSlice returns
        // true for a normal value. Readers still get offset 0 and the real length, and WriteRlp
        // resets the sequence on the next write so it cannot advance to a wrong non-zero offset.
        private const ulong RlpSliceFlag = 1UL << 63;
        private const ulong RlpSliceLengthMask = 0xFFFFFFFFUL;
        private const int RlpSliceOffsetShift = 32;
        private const ulong RlpSliceOffsetMask = 0x7FFFFFFFUL;

        private static bool IsRlpSlice(ulong value) => (value & RlpSliceFlag) != 0;

        /// <summary>
        /// Atomically read _rlp using seqlock: retry if a concurrent write is detected.
        /// Memory barriers ensure ARM64 correctness (matching SeqlockCache/KeccakCache patterns).
        /// </summary>
        private CappedArray<byte> ReadRlp()
        {
            SpinWait spin = default;
            ulong seqBefore, seqAfter;
            byte[]? array;
            while (true)
            {
                seqBefore = Volatile.Read(ref _rlpSeqAndLength);
                if (IsRlpSlice(seqBefore))
                {
                    array = Volatile.Read(ref _rlpArray);
                    seqAfter = Volatile.Read(ref _rlpSeqAndLength);
                    if (seqBefore == seqAfter)
                    {
                        return array is null
                            ? default
                            : new CappedArray<byte>(
                                array,
                                (int)((seqBefore >> RlpSliceOffsetShift) & RlpSliceOffsetMask),
                                (int)(seqBefore & RlpSliceLengthMask));
                    }

                    spin.SpinOnce();
                    continue;
                }

                if ((seqBefore >> 32 & 1) != 0) { spin.SpinOnce(); continue; }
                if (!Sse.IsSupported) Interlocked.MemoryBarrier();
                array = _rlpArray;
                if (!Sse.IsSupported) Interlocked.MemoryBarrier();
                seqAfter = Volatile.Read(ref _rlpSeqAndLength);
                if (seqBefore == seqAfter) break;
                spin.SpinOnce();
            }

            return array is null ? default : new CappedArray<byte>(array, (int)(seqBefore & 0xFFFFFFFF));
        }

        /// <summary>
        /// Atomically write _rlp using seqlock: odd sequence signals write-in-progress.
        /// CAS on even sequences only — if another writer is active (odd), spin until it completes.
        /// Last writer wins: all writers write the same resolved data for a given node.
        /// Sequence uses bits 1-31; bit 0 is the lock flag and bit 31 overlaps the slice flag.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)] // CAS dominates latency; avoid code bloat at 5+ call sites
        internal void WriteRlp(CappedArray<byte> value)
        {
            SpinWait spin = default;
            while (true)
            {
                ulong current = Volatile.Read(ref _rlpSeqAndLength);
                // If a normal-mode sequence reached the slice flag bit, reset before the next
                // completed write can publish a non-zero slice offset.
                uint seq = IsRlpSlice(current) ? 0 : (uint)(current >> 32);
                if ((seq & 1) != 0)
                {
                    // Another writer is active — spin until it completes
                    spin.SpinOnce();
                    continue;
                }
                // Set lock bit (odd) — seq | 1 is always odd regardless of overflow
                ulong writing = (ulong)(seq | 1) << 32;
                if (Interlocked.CompareExchange(ref _rlpSeqAndLength, writing, current) == current)
                {
                    Volatile.Write(ref _rlpArray, value.UnderlyingArray);
                    // Advance sequence by 2 and clear lock bit (even), store final length
                    uint doneSeq = (seq + 2) & 0xFFFFFFFE;
                    Volatile.Write(ref _rlpSeqAndLength, CreateRlpMetadata(value, doneSeq));
                    return;
                }
                spin.SpinOnce(); // CAS failed — another writer raced; back off before retry
            }
        }

        /// <summary>
        /// Direct field initialization — no seqlock needed during single-threaded construction.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void InitRlp(CappedArray<byte> value)
        {
            _rlpArray = value.UnderlyingArray;
            _rlpSeqAndLength = CreateRlpMetadata(value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void InitRlpSlice(byte[] parentRlp, int offset, int length)
        {
            _rlpArray = parentRlp;
            _rlpSeqAndLength = CreateRlpSliceMetadata(offset, length);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong CreateRlpMetadata(CappedArray<byte> value, uint normalSeq = 0) =>
            value.Offset == 0
                ? ((ulong)normalSeq << 32) | (uint)value.Length
                : CreateRlpSliceMetadata(value.Offset, value.Length);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong CreateRlpSliceMetadata(int offset, int length) =>
            RlpSliceFlag | ((ulong)(uint)offset << RlpSliceOffsetShift) | (uint)length;

        /// <summary>
        /// Sealed node is the one that is already immutable except for reference counting and resolving existing data
        /// </summary>
        public bool IsSealed => !IsDirty;

        public bool IsPersisted
        {
            get => (Volatile.Read(ref _blockAndFlags) & _persistedMask) != 0;
            set
            {
                byte previousValue = Volatile.Read(ref _blockAndFlags);
                byte currentValue;
                do
                {
                    currentValue = previousValue;
                    byte newValue = (byte)(value ? (currentValue | _persistedMask) : (currentValue & ~_persistedMask));
                    previousValue = Interlocked.CompareExchange(ref _blockAndFlags, newValue, currentValue);
                } while (previousValue != currentValue);
            }
        }

        public bool IsBoundaryProofNode
        {
            get => (Volatile.Read(ref _blockAndFlags) & _boundaryProof) != 0;
            set
            {
                byte previousValue = Volatile.Read(ref _blockAndFlags);
                byte currentValue;
                do
                {
                    currentValue = previousValue;
                    byte newValue = (byte)(value ? (currentValue | _boundaryProof) : (currentValue & ~_boundaryProof));
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
            byte previousValue = Volatile.Read(ref _blockAndFlags);
            byte currentValue;
            do
            {
                if ((previousValue & _dirtyMask) == 0)
                {
                    ThrowAlreadySealed();
                }

                currentValue = previousValue;
                byte newValue = (byte)(currentValue & ~_dirtyMask);
                previousValue = Interlocked.CompareExchange(ref _blockAndFlags, newValue, currentValue);
            } while (previousValue != currentValue);

            [DoesNotReturn, StackTraceHidden]
            void ThrowAlreadySealed() => throw new InvalidOperationException($"{nameof(TrieNode)} {this} is already sealed.");
        }

        /// <summary>
        /// Cheap presence check for the inline keccak; see <see cref="TryGetKeccak(out ValueHash256)"/>
        /// for safe value access.
        /// </summary>
        public bool HasKeccak => (Volatile.Read(ref _blockAndFlags) & _hasKeccakMask) != 0;

        /// <summary>
        /// Reads the inline node keccak under the <c>_keccakSeq</c> seqlock. Returns <c>false</c>
        /// when the node has no keccak set or when a concurrent clear is observed mid-read.
        /// </summary>
        /// <remarks>
        /// Hot trie paths must use this (or <see cref="KeccakValue"/>) instead of the public
        /// <see cref="Keccak"/> getter, which materializes a <see cref="Hash256"/>.
        /// </remarks>
        public bool TryGetKeccak(out ValueHash256 keccak)
        {
            SpinWait spin = default;
            while (true)
            {
                byte flags = Volatile.Read(ref _blockAndFlags);
                if ((flags & _hasKeccakMask) == 0)
                {
                    keccak = default;
                    return false;
                }

                uint seqBefore = Volatile.Read(ref _keccakSeq);
                if ((seqBefore & 1) != 0)
                {
                    spin.SpinOnce();
                    continue;
                }

                if (!Sse.IsSupported) Interlocked.MemoryBarrier();
                ValueHash256 value = _keccakValue;
                if (!Sse.IsSupported) Interlocked.MemoryBarrier();

                uint seqAfter = Volatile.Read(ref _keccakSeq);
                if (seqBefore != seqAfter)
                {
                    spin.SpinOnce();
                    continue;
                }

                // A concurrent ClearKeccak bumps the seq; the loop above catches it. A
                // re-check of the bit covers the rare case of clear+set producing matching
                // seq values across iterations.
                if ((Volatile.Read(ref _blockAndFlags) & _hasKeccakMask) == 0)
                {
                    keccak = default;
                    return false;
                }

                keccak = value;
                return true;
            }
        }

        /// <summary>By-value accessor; throws if no keccak is set. Use <see cref="HasKeccak"/> first.</summary>
        public ValueHash256 KeccakValue
        {
            get
            {
                if (TryGetKeccak(out ValueHash256 keccak)) return keccak;
                return ThrowMissingKeccakValue();

                [DoesNotReturn, StackTraceHidden]
                static ValueHash256 ThrowMissingKeccakValue() =>
                    throw new InvalidOperationException($"{nameof(TrieNode)} has no {nameof(Keccak)}.");
            }
        }

        /// <summary>
        /// Cold-path compatibility shim that materializes a <see cref="Hash256"/>. Hot trie
        /// code must use <see cref="TryGetKeccak(out ValueHash256)"/> or <see cref="KeccakValue"/>
        /// to avoid the per-read allocation.
        /// </summary>
        public Hash256? Keccak
        {
            get => TryGetKeccak(out ValueHash256 keccak) ? new Hash256(in keccak) : null;
            internal set
            {
                if (value is null)
                {
                    ClearKeccak();
                }
                else
                {
                    SetKeccak(in value.ValueHash256);
                }
            }
        }

        /// <summary>
        /// Publishes <paramref name="keccak"/> under the seqlock and sets <see cref="HasKeccak"/>.
        /// CAS dominates latency; <see cref="MethodImplOptions.NoInlining"/> avoids code bloat
        /// at the multiple call sites that resolve hashes.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void SetKeccak(in ValueHash256 keccak)
        {
            SpinWait spin = default;
            while (true)
            {
                uint current = Volatile.Read(ref _keccakSeq);
                if ((current & 1) != 0)
                {
                    spin.SpinOnce();
                    continue;
                }

                if (Interlocked.CompareExchange(ref _keccakSeq, current | 1, current) == current)
                {
                    _keccakValue = keccak;

                    // Set the has-keccak bit while seq is locked so any concurrent reader
                    // either sees the in-progress odd seq or the completed even seq with
                    // both bit and value coherent.
                    byte flags = Volatile.Read(ref _blockAndFlags);
                    while (true)
                    {
                        byte newFlags = (byte)(flags | _hasKeccakMask);
                        byte prev = Interlocked.CompareExchange(ref _blockAndFlags, newFlags, flags);
                        if (prev == flags) break;
                        flags = prev;
                    }

                    Volatile.Write(ref _keccakSeq, (current + 2) & 0xFFFFFFFE);
                    return;
                }

                spin.SpinOnce();
            }
        }

        /// <summary>
        /// Clears <see cref="HasKeccak"/> under the seqlock. The 32-byte value is left intact
        /// because the bit is authoritative; readers test the bit before returning the value.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void ClearKeccak()
        {
            SpinWait spin = default;
            while (true)
            {
                uint current = Volatile.Read(ref _keccakSeq);
                if ((current & 1) != 0)
                {
                    spin.SpinOnce();
                    continue;
                }

                if (Interlocked.CompareExchange(ref _keccakSeq, current | 1, current) == current)
                {
                    byte flags = Volatile.Read(ref _blockAndFlags);
                    while (true)
                    {
                        if ((flags & _hasKeccakMask) == 0) break;
                        byte newFlags = (byte)(flags & ~_hasKeccakMask);
                        byte prev = Interlocked.CompareExchange(ref _blockAndFlags, newFlags, flags);
                        if (prev == flags) break;
                        flags = prev;
                    }

                    Volatile.Write(ref _keccakSeq, (current + 2) & 0xFFFFFFFE);
                    return;
                }

                spin.SpinOnce();
            }
        }

        /// <summary>
        /// Copy non-keccak flags (<see cref="_dirtyMask"/>, <see cref="_persistedMask"/>,
        /// <see cref="_boundaryProof"/>) from <paramref name="source"/> onto <c>this</c>.
        /// Used by the typed-resolve path so a freshly-allocated derived instance inherits
        /// dirty / boundary-proof state from the placeholder it replaces. Optionally OR-in
        /// <see cref="_persistedMask"/> when the caller pulled RLP from the backing store.
        /// </summary>
        internal void CopyFlagsFrom(TrieNode source, bool markPersisted)
        {
            byte sourceFlags = (byte)(Volatile.Read(ref source._blockAndFlags) & (_dirtyMask | _persistedMask | _boundaryProof));
            if (markPersisted) sourceFlags |= _persistedMask;

            byte previous = Volatile.Read(ref _blockAndFlags);
            byte current;
            do
            {
                current = previous;
                byte next = (byte)((current & _hasKeccakMask) | sourceFlags);
                if (next == current) return;
                previous = Interlocked.CompareExchange(ref _blockAndFlags, next, current);
            } while (previous != current);
        }

        public bool HasRlp => Volatile.Read(ref _rlpArray) is not null;

        public CappedArray<byte> FullRlp => ReadRlp();

        public ValueRlpStream RlpStream
        {
            get
            {
                CappedArray<byte> rlp = ReadRlp();
                return rlp.IsNull ? default : new ValueRlpStream(rlp);
            }
        }

        // Virtual: derived sealed classes return Branch / Leaf / Extension. Bare
        // TrieNode instances are Unknown placeholders produced by legacy resolver
        // contracts that have not yet been migrated to typed load / decode.
        public virtual NodeType NodeType => NodeType.Unknown;

        public bool IsLeaf => NodeType == NodeType.Leaf;

        public bool IsBranch => NodeType == NodeType.Branch;

        public bool IsExtension => NodeType == NodeType.Extension;

        public byte[]? Key
        {
            get => KeyInternal;
            internal set
            {
                if (this is not (TrieNodeLeaf or TrieNodeExtension))
                {
                    ThrowDoesNotSupportKey();
                }

                if (IsSealed)
                {
                    if (KeyInternal.AsSpan().SequenceEqual(value))
                    {
                        // No change, parallel read
                        return;
                    }

                    ThrowAlreadySealed();
                }

                KeyInternal = value;
                ClearKeccak();

                [DoesNotReturn, StackTraceHidden]
                void ThrowDoesNotSupportKey() => throw new InvalidOperationException(
                        $"{NodeType} {this} is does not support having a {nameof(Key)}.");

                [DoesNotReturn, StackTraceHidden]
                void ThrowAlreadySealed() => throw new InvalidOperationException(
                        $"{nameof(TrieNode)} {this} is already sealed when setting {nameof(Key)}.");
            }
        }

        public CappedArray<byte> Value
        {
            get
            {
                if (this is TrieNodeLeaf leafNode)
                {
                    return leafNode._value;
                }

                // branches that we use for state will never have value set as all the keys are equal length
                return CappedArray<byte>.Empty;
            }
            set
            {
                if (this is not TrieNodeLeaf leafNode)
                {
                    ThrowNoValueOnBranches();
                }

                CappedArray<byte> current = leafNode._value;
                if (IsSealed)
                {
                    if ((current.IsNull && value.IsNull) || (!current.IsNull && !value.IsNull && current.AsSpan().SequenceEqual(value.AsSpan())))
                    {
                        // No change, parallel read
                        return;
                    }

                    ThrowAlreadySealed();
                }

                leafNode._value = value;

                [DoesNotReturn, StackTraceHidden]
                void ThrowAlreadySealed() => throw new InvalidOperationException(
                        $"{nameof(TrieNode)} {this} is already sealed when setting {nameof(Value)}.");

                [DoesNotReturn, StackTraceHidden]
                static void ThrowNoValueOnBranches() => throw new TrieException("Optimized Patricia Trie does not support setting values on branches.");
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

                return false;
            }
        }

        // Copy constructor shared by the typed CloneTyped overrides. Sets the
        // dirty flag; subclasses copy their own shape fields after the base ctor runs.
        private protected TrieNode(TrieNode node) => _blockAndFlags = _dirtyMask;

        // Internal-only constructors used by the typed subclasses to plumb the
        // initial Keccak and / or RLP through the seqlocked fields. The shape
        // (branch / leaf / extension) is established by the runtime type of the
        // subclass; there is no `INodeData` indirection any more.
        // Dirty by default — typed nodes that are being constructed in-memory before
        // RLP encoding need IsDirty=true so that mutation paths (key/value setters)
        // do not see them as sealed.
        private protected TrieNode() => _blockAndFlags = _dirtyMask;

        private protected TrieNode(in ValueHash256 keccak) => SetKeccak(in keccak);

        private protected TrieNode(CappedArray<byte> rlp, bool isDirty)
        {
            if (isDirty)
            {
                _blockAndFlags |= _dirtyMask;
            }
            InitRlp(rlp);
        }

        private protected TrieNode(CappedArray<byte> rlp, in ValueHash256 keccak)
        {
            SetKeccak(in keccak);
            InitRlp(rlp);
        }

        // The public legacy NodeType-taking constructors are gone. TrieNode is abstract;
        // unresolved by-hash placeholders allocate TrieNodePlaceholder; resolved nodes
        // are TrieNodeBranch / TrieNodeLeaf / TrieNodeExtension produced by DecodeNode.

        /// <summary>
        /// Build a fully decoded inline child node from a parent RLP slice. Inline nodes
        /// have no separate Keccak (RLP &lt; 32 bytes) and share the parent buffer.
        /// </summary>
        /// <remarks>
        /// Phase B requires that no <see cref="NodeType.Unknown"/> node ever leaves
        /// <see cref="DecodeChildReference"/>: the parent RLP is already in hand, so
        /// header decoding (no IO) gives the correct typed runtime shape immediately.
        /// </remarks>
        private static TrieNode CreateInlineChild(in TreePath parentPath, CappedArray<byte> parentRlp, int offset, int length)
        {
            int sliceOffset = parentRlp.Offset + offset;
            CappedArray<byte> slice = new(parentRlp.UnderlyingArray!, sliceOffset, length);
            TrieNode? typed;
            try
            {
                typed = AllocateTypedFromRlp(slice, bufferPool: null, out int numberOfItems);
                if (typed is null)
                {
                    ThrowUnexpectedNumberOfItems(numberOfItems, parentPath);
                }
            }
            catch (RlpException rlpException)
            {
                ThrowInlineDecodingError(rlpException, parentPath);
                return null!; // unreachable
            }

            typed.InitRlpSlice(parentRlp.UnderlyingArray!, sliceOffset, length);
            return typed;

            [DoesNotReturn, StackTraceHidden]
            static void ThrowInlineDecodingError(RlpException rlpException, in TreePath parentPath) =>
                throw new TrieNodeException("Error when decoding inline child", parentPath, Nethermind.Core.Crypto.Keccak.Zero, rlpException);

            [DoesNotReturn, StackTraceHidden]
            static void ThrowUnexpectedNumberOfItems(int numberOfItems, in TreePath parentPath) =>
                throw new TrieNodeException(
                    $"Unexpected number of items = {numberOfItems} when decoding inline child from RLP",
                    parentPath, Nethermind.Core.Crypto.Keccak.Zero);
        }

        // Virtual hooks that the typed subclasses override to expose their shape data.
        // Base TrieNode (Unknown placeholder) throws because Unknown nodes carry no
        // child slots or key / value state until they are resolved into a typed node.

        /// <summary>Ref-returning access to a child slot. Branches expose 16 slots;
        /// extensions expose one (index 0) which actually stores the child reference.
        /// Leaves have no slots.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal virtual ref TrieNode? GetSlotRef(int index) =>
            throw new InvalidOperationException($"GetSlotRef called on an unresolved TrieNode ({this}).");

        /// <summary>Hex-prefixed key for leaf and extension nodes; null for branches
        /// and unresolved placeholders.</summary>
        internal virtual byte[]? KeyInternal
        {
            get => null;
            set => throw new InvalidOperationException($"Setting key on a non-key-bearing TrieNode ({this}).");
        }

        /// <summary>Ref to the value bytes for leaf nodes. The base override throws
        /// so that hot paths can branch on `this is TrieNodeLeaf` first.</summary>
        internal virtual ref CappedArray<byte> ValueRef =>
            throw new InvalidOperationException($"ValueRef called on a non-leaf TrieNode ({this}).");

        /// <summary>Ref to the storage-root reference for leaf nodes (accounts in the
        /// state trie). Throws on other shapes.</summary>
        internal virtual ref TrieNode? StorageRootRef =>
            throw new InvalidOperationException($"StorageRootRef called on a non-leaf TrieNode ({this}).");

        /// <summary>Estimated heap footprint of the typed shape data: 16 * RefSize for
        /// branches, key + value + storage-root for leaves, key + child for extensions.
        /// Unknown placeholders carry no shape data.</summary>
        internal virtual int MemorySizeOfData => 0;

        /// <summary>Type-preserving shallow clone of the shape data plus the dirty flag.
        /// Each subclass returns its own runtime type so `this is TrieNodeX` checks
        /// remain stable after mutation.</summary>
        internal abstract TrieNode CloneTyped();

        public override string ToString() =>
#if DEBUG
            $"[{NodeType}({(FullRlp.IsNotNullOrEmpty ? FullRlp.Length : 0)}){(FullRlp.IsNotNullOrEmpty && FullRlp.Length < 32 ? $"{FullRlp.AsSpan().ToHexString()}" : "")}" +
                $"|{Id}|{Keccak}|D:{IsDirty}|S:{IsSealed}|P:{IsPersisted}|";
#else
            $"[{NodeType}({(FullRlp.IsNotNullOrEmpty ? FullRlp.Length : 0)})|{Keccak?.ToShortString()}|D:{IsDirty}|S:{IsSealed}|P:{IsPersisted}|";
#endif


        /// <summary>
        /// Resolve <paramref name="node"/> to a fully-decoded typed instance
        /// (<see cref="TrieNodeBranch"/>, <see cref="TrieNodeLeaf"/>, or
        /// <see cref="TrieNodeExtension"/>). When <paramref name="node"/> is already
        /// typed this is a no-op. When it is a base <see cref="TrieNode"/> placeholder
        /// (<see cref="NodeType.Unknown"/>), a new typed instance is allocated from
        /// the RLP payload and <paramref name="node"/> is rebound to it; the original
        /// placeholder is discarded.
        /// </summary>
        /// <remarks>
        /// C# cannot change the runtime type of an existing object, so this contract
        /// rebinds the caller's reference rather than mutating <paramref name="node"/>
        /// in place. The <c>ref</c> parameter forces every caller to surface the
        /// rebind, which keeps the cache slot, branch child slot, root ref, snap-stitch
        /// holder, or local in sync with the typed result. The B3 invariant is: every
        /// <see cref="TrieNode"/> reachable past a successful resolve is one of the
        /// typed derived classes. Throws <see cref="TrieNodeException"/> wrapping the
        /// underlying <see cref="RlpException"/> on malformed input or a
        /// <see cref="TrieException"/> when RLP is missing entirely.
        /// </remarks>
        public static void ResolveNode(ref TrieNode node, ITrieNodeResolver tree, in TreePath path,
            ReadFlags readFlags = ReadFlags.None, ICappedArrayPool? bufferPool = null)
        {
            if (node.NodeType != NodeType.Unknown)
            {
                return;
            }

            try
            {
                node = ResolveUnknown(node, tree, path, readFlags, bufferPool);
            }
            catch (RlpException rlpException)
            {
                throw new TrieNodeException($"Error when decoding node {node.Keccak}", path,
                    node.Keccak ?? Nethermind.Core.Crypto.Keccak.Zero, rlpException);
            }
        }

        private static TrieNode ResolveUnknown(TrieNode placeholder, ITrieNodeResolver tree, in TreePath path,
            ReadFlags readFlags, ICappedArrayPool? bufferPool)
        {
            CappedArray<byte> rlp = placeholder.ReadRlp();
            bool loadedFromStore = false;
            if (rlp.IsNull)
            {
                if (!placeholder.TryGetKeccak(out ValueHash256 keccak))
                {
                    ThrowMissingKeccak();
                }

                byte[]? fullRlp = tree.LoadRlp(path, in keccak, readFlags);

                if (fullRlp == null)
                {
                    ThrowNullRlp(placeholder);
                }

                rlp = new CappedArray<byte>(fullRlp);
                loadedFromStore = true;
            }

            TrieNode? typed = AllocateTypedFromRlp(rlp, bufferPool, out int numberOfItems);
            if (typed is null)
            {
                ThrowUnexpectedNumberOfItems(numberOfItems, path, placeholder);
            }

            typed.InitRlp(rlp);
            if (placeholder.TryGetKeccak(out ValueHash256 placeholderKeccak))
            {
                typed.SetKeccak(in placeholderKeccak);
            }
            // Preserve dirty / boundary-proof flags from the placeholder; mark persisted
            // when we just pulled the RLP from the backing store.
            typed.CopyFlagsFrom(placeholder, markPersisted: loadedFromStore || placeholder.IsPersisted);
            return typed;

            [DoesNotReturn, StackTraceHidden]
            static void ThrowMissingKeccak() => throw new TrieException("Unable to resolve node without Keccak");

            [DoesNotReturn, StackTraceHidden]
            static void ThrowNullRlp(TrieNode placeholder) => throw new TrieException($"Trie returned a NULL RLP for node {placeholder.Keccak}");

            [DoesNotReturn, StackTraceHidden]
            static void ThrowUnexpectedNumberOfItems(int numberOfItems, in TreePath path, TrieNode placeholder) =>
                throw new TrieNodeException(
                    $"Unexpected number of items = {numberOfItems} when decoding a node from RLP ({placeholder.FullRlp.AsSpan().ToHexString()})",
                    path, placeholder.Keccak ?? Nethermind.Core.Crypto.Keccak.Zero);
        }

        /// <summary>
        /// Try-style sibling of <see cref="ResolveNode(ref TrieNode, ITrieNodeResolver, in TreePath, ReadFlags, ICappedArrayPool?)"/>.
        /// On success rebinds <paramref name="node"/> to the typed instance and returns
        /// <c>true</c>; on failure leaves <paramref name="node"/> untouched and returns
        /// <c>false</c>. Failure cases: store cannot produce RLP, placeholder has no
        /// keccak to look up by, or decoding throws an <see cref="RlpException"/>.
        /// </summary>
        public static bool TryResolveNode(ref TrieNode node, ITrieNodeResolver tree, ref TreePath path,
            ReadFlags readFlags = ReadFlags.None, ICappedArrayPool? bufferPool = null)
        {
            if (node.NodeType != NodeType.Unknown)
            {
                return true;
            }

            try
            {
                CappedArray<byte> rlp = node.ReadRlp();
                if (rlp.IsNull)
                {
                    if (!node.TryGetKeccak(out ValueHash256 keccak))
                    {
                        return false;
                    }

                    byte[]? fullRlp = tree.TryLoadRlp(path, in keccak, readFlags);
                    if (fullRlp is null)
                    {
                        return false;
                    }

                    rlp = new CappedArray<byte>(fullRlp);
                }

                TrieNode? typed = AllocateTypedFromRlp(rlp, bufferPool, out _);
                if (typed is null)
                {
                    return false;
                }

                typed.InitRlp(rlp);
                if (node.TryGetKeccak(out ValueHash256 nodeKeccak))
                {
                    typed.SetKeccak(in nodeKeccak);
                }
                typed.CopyFlagsFrom(node, markPersisted: true);
                node = typed;
                return true;
            }
            catch (RlpException)
            {
                return false;
            }
        }

        /// <summary>
        /// Decode RLP into a fully resolved <see cref="TrieNode"/> with the correct
        /// runtime type (<see cref="TrieNodeBranch"/>, <see cref="TrieNodeLeaf"/>, or
        /// <see cref="TrieNodeExtension"/>). Used by resolvers that fuse load+decode so
        /// they never publish an <see cref="NodeType.Unknown"/> placeholder.
        /// </summary>
        /// <remarks>
        /// The returned node carries the supplied <paramref name="hash"/> as its keccak
        /// and is marked persisted. Throws <see cref="TrieNodeException"/> wrapping the
        /// underlying <see cref="RlpException"/> on malformed input.
        /// </remarks>
        public static TrieNode DecodeNode(in TreePath path, in ValueHash256 hash, byte[] rlp,
            ICappedArrayPool? bufferPool = null)
        {
            if (rlp is null) ThrowNullRlp(in hash);

            TrieNode? typed;
            try
            {
                typed = AllocateTypedFromRlp(new CappedArray<byte>(rlp), bufferPool, out int numberOfItems);
                if (typed is null)
                {
                    ThrowUnexpectedNumberOfItems(numberOfItems, path, in hash, rlp);
                }
            }
            catch (RlpException rlpException)
            {
                ThrowDecodingError(rlpException, path, in hash);
                return null!; // unreachable
            }

            typed.InitRlp(new CappedArray<byte>(rlp));
            typed.SetKeccak(in hash);
            typed.IsPersisted = true;
            // Decoded-from-RLP nodes represent committed state. Seal so commit walks
            // do not treat them as dirty and re-encode them into a parent's RLP
            // (mutation paths that legitimately edit a loaded node call CloneTyped
            // first, producing a fresh dirty copy).
            typed.Seal();
            return typed;

            [DoesNotReturn, StackTraceHidden]
            static void ThrowNullRlp(in ValueHash256 hash) => throw new TrieException($"Cannot decode node {hash} from null RLP");

            [DoesNotReturn, StackTraceHidden]
            static void ThrowDecodingError(RlpException rlpException, in TreePath path, in ValueHash256 hash) =>
                throw new TrieNodeException($"Error when decoding node {hash}", path, new Hash256(in hash), rlpException);

            [DoesNotReturn, StackTraceHidden]
            static void ThrowUnexpectedNumberOfItems(int numberOfItems, in TreePath path, in ValueHash256 hash, byte[] rlp) =>
                throw new TrieNodeException(
                    $"Unexpected number of items = {numberOfItems} when decoding a node from RLP ({rlp.AsSpan().ToHexString()})",
                    path, new Hash256(in hash));
        }

        /// <summary>
        /// Like <see cref="DecodeNode"/> but returns <c>false</c> when <paramref name="rlp"/>
        /// is null or decoding fails. Used by <c>TryGetOrLoadNode</c> paths where the
        /// resolver still wants to deliver a typed node without placeholder semantics.
        /// </summary>
        public static bool TryDecodeNode(in TreePath path, in ValueHash256 hash, byte[]? rlp,
            [NotNullWhen(true)] out TrieNode? node, ICappedArrayPool? bufferPool = null)
        {
            if (rlp is null)
            {
                node = null;
                return false;
            }

            TrieNode? typed;
            try
            {
                typed = AllocateTypedFromRlp(new CappedArray<byte>(rlp), bufferPool, out _);
            }
            catch (RlpException)
            {
                node = null;
                return false;
            }

            if (typed is null)
            {
                node = null;
                return false;
            }

            typed.InitRlp(new CappedArray<byte>(rlp));
            typed.SetKeccak(in hash);
            typed.IsPersisted = true;
            typed.Seal();
            node = typed;
            return true;
        }

        /// <summary>
        /// Decode <paramref name="rlp"/> into a typed <see cref="TrieNode"/> without a known
        /// Keccak. The returned node carries the RLP and is marked persisted but has no
        /// hash set; callers compute the hash on demand via <see cref="ResolveKey"/>.
        /// </summary>
        /// <remarks>
        /// Used by sync-store verification contexts that receive a serialized root node and
        /// need a typed <see cref="TrieNode"/> immediately, without going through the
        /// placeholder + <see cref="ResolveNode"/> two-step that previously produced an
        /// <see cref="NodeType.Unknown"/> stand-in.
        /// </remarks>
        public static TrieNode DecodeRootFromRlp(byte[] rlp, ICappedArrayPool? bufferPool = null)
        {
            ArgumentNullException.ThrowIfNull(rlp);

            TrieNode? typed;
            try
            {
                typed = AllocateTypedFromRlp(new CappedArray<byte>(rlp), bufferPool, out int numberOfItems);
                if (typed is null)
                {
                    ThrowUnexpectedNumberOfItems(numberOfItems, rlp);
                }
            }
            catch (RlpException rlpException)
            {
                ThrowDecodingError(rlpException);
                return null!; // unreachable
            }

            typed.InitRlp(new CappedArray<byte>(rlp));
            typed.IsPersisted = true;
            return typed;

            [DoesNotReturn, StackTraceHidden]
            static void ThrowDecodingError(RlpException rlpException) =>
                throw new TrieNodeException("Error when decoding root node from RLP", default, Nethermind.Core.Crypto.Keccak.Zero, rlpException);

            [DoesNotReturn, StackTraceHidden]
            static void ThrowUnexpectedNumberOfItems(int numberOfItems, byte[] rlp) =>
                throw new TrieNodeException(
                    $"Unexpected number of items = {numberOfItems} when decoding a node from RLP ({rlp.AsSpan().ToHexString()})",
                    default, Nethermind.Core.Crypto.Keccak.Zero);
        }

        /// <summary>
        /// Decode an inline child whose RLP is already in hand (length &lt; 32) into a typed
        /// <see cref="TrieNode"/>. Inline nodes have no separate Keccak; their identity is
        /// the RLP itself.
        /// </summary>
        /// <remarks>
        /// Used by snap-sync boundary stitching where a branch slot inlines a small child
        /// node. Previously the call site built an <see cref="NodeType.Unknown"/> placeholder
        /// with the inline RLP and then invoked <see cref="ResolveNode"/> with a
        /// <see cref="NullTrieNodeResolver"/>; this helper folds that into one typed
        /// decode so no placeholder is ever observable.
        /// </remarks>
        public static TrieNode DecodeInlineFromRlp(byte[] inlineRlp, in TreePath path, ICappedArrayPool? bufferPool = null)
        {
            ArgumentNullException.ThrowIfNull(inlineRlp);

            TrieNode? typed;
            try
            {
                typed = AllocateTypedFromRlp(new CappedArray<byte>(inlineRlp), bufferPool, out int numberOfItems);
                if (typed is null)
                {
                    ThrowUnexpectedNumberOfItems(numberOfItems, path);
                }
            }
            catch (RlpException rlpException)
            {
                ThrowDecodingError(rlpException, path);
                return null!; // unreachable
            }

            typed.InitRlp(new CappedArray<byte>(inlineRlp));
            return typed;

            [DoesNotReturn, StackTraceHidden]
            static void ThrowDecodingError(RlpException rlpException, in TreePath path) =>
                throw new TrieNodeException("Error when decoding inline child", path, Nethermind.Core.Crypto.Keccak.Zero, rlpException);

            [DoesNotReturn, StackTraceHidden]
            static void ThrowUnexpectedNumberOfItems(int numberOfItems, in TreePath path) =>
                throw new TrieNodeException(
                    $"Unexpected number of items = {numberOfItems} when decoding inline child from RLP",
                    path, Nethermind.Core.Crypto.Keccak.Zero);
        }

        /// <summary>
        /// Peek the RLP header to determine branch/leaf/extension shape and allocate
        /// the matching typed <see cref="TrieNode"/> derived instance with its shape
        /// state initialized. Returns <c>null</c> when the RLP contains fewer
        /// than two items. The returned node carries no RLP or keccak yet; callers
        /// publish those after the typed shell is in hand.
        /// </summary>
        private static TrieNode? AllocateTypedFromRlp(CappedArray<byte> rlp, ICappedArrayPool? bufferPool, out int itemsCount)
        {
            Metrics.TreeNodeRlpDecodings++;

            ValueRlpStream rlpStream = new(rlp);
            rlpStream.ReadSequenceLength();
            int numberOfItems = itemsCount = rlpStream.PeekNumberOfItemsRemaining(null, 3);

            if (numberOfItems < 2)
            {
                return null;
            }
            if (numberOfItems > 2)
            {
                return new TrieNodeBranch();
            }

            ReadOnlySpan<byte> valueSpan = rlpStream.DecodeByteArraySpan();
            (byte[] key, bool isLeaf) = HexPrefix.FromBytes(valueSpan);
            if (isLeaf)
            {
                valueSpan = rlpStream.DecodeByteArraySpan();
                CappedArray<byte> buffer = bufferPool.SafeRent(valueSpan.Length);
                valueSpan.CopyTo(buffer.AsSpan());
                return new TrieNodeLeaf(key, buffer);
            }

            return new TrieNodeExtension(key);
        }

        internal void PreDecodeChildrenIfBranch(ref TreePath path)
        {
            if (this is not TrieNodeBranch || !IsSealed)
            {
                return;
            }

            CappedArray<byte> rlp = ReadRlp();
            if (rlp.IsNull)
            {
                return;
            }

            int originalPathLength = path.Length;
            try
            {
                ValueRlpStream rlpStream = new(rlp);
                rlpStream.Reset();
                rlpStream.SkipLength();
                path.AppendMut(0);

                for (int i = 0; i < BranchesCount; i++)
                {
                    path.SetLast(i);
                    ref TrieNode? data = ref GetSlotRef(i);
                    if (Volatile.Read(ref data) is not null)
                    {
                        rlpStream.SkipItem();
                        continue;
                    }

                    // PreDecode is best-effort population without a resolver; only publish
                    // the empty sentinel and inline children. By-hash references stay null
                    // and are decoded on first read with a resolver in hand.
                    int prefix = rlpStream.PeekByte();
                    if (prefix is 0 or 128)
                    {
                        rlpStream.SkipItem();
                        PublishChild(ref data, NullNode);
                    }
                    else if (prefix == 160)
                    {
                        // By-hash: leave slot null; ResolveChildWithChildPath decodes lazily.
                        rlpStream.SkipItem();
                    }
                    else
                    {
                        int offset = rlpStream.Position;
                        int length = rlpStream.PeekNextRlpLength();
                        TrieNode inline = CreateInlineChild(in path, rlp, offset, length);
                        rlpStream.SkipBytes(length);
                        PublishChild(ref data, inline);
                    }
                }
            }
            finally
            {
                path.TruncateMut(originalPathLength);
            }
        }


        public void ResolveKey(ITrieNodeResolver tree, ref TreePath path,
            ICappedArrayPool? bufferPool = null, bool canBeParallel = true)
        {
            if (HasKeccak)
            {
                // please note it is totally fine to leave the RLP null here
                // this node will simply act as a ref only node (a ref to some node with unresolved data in the DB)
                return;
            }

            if (TryGenerateKey(tree, ref path, out ValueHash256 keccak, bufferPool, canBeParallel))
            {
                SetKeccak(in keccak);
            }
        }

        /// <summary>
        /// Non-allocating sibling of <see cref="GenerateKey"/>. Returns <c>true</c> when the encoded RLP is
        /// long enough (>= 32 bytes) or this is the root, in which case <paramref name="keccak"/> holds the
        /// computed hash. Returns <c>false</c> for short inline child nodes that have no separate hash.
        /// </summary>
        public bool TryGenerateKey(ITrieNodeResolver tree, ref TreePath path, out ValueHash256 keccak,
            ICappedArrayPool? bufferPool = null, bool canBeParallel = true)
        {
            bool isRoot = path.Length == 0;
            CappedArray<byte> rlp = ReadRlp();
            if (rlp.IsNull || IsDirty)
            {
                CappedArray<byte> oldRlp = rlp.IsNotNull ? rlp : CappedArray<byte>.Empty;
                CappedArray<byte> fullRlp = NodeType == NodeType.Branch
                    ? TrieNodeDecoder.RlpEncodeBranch(this, tree, ref path, bufferPool,
                        canBeParallel: isRoot && canBeParallel)
                    : RlpEncode(tree, ref path, bufferPool, canBeParallel);

                if (oldRlp.IsNotNullOrEmpty)
                {
                    bufferPool.SafeReturn(oldRlp);
                }

                WriteRlp(rlp = fullRlp);
            }

            /* nodes that are descendants of other nodes are stored inline
             * if their serialized length is less than Keccak length
             * */
            if (rlp.Length >= 32 || isRoot)
            {
                Metrics.TreeNodeHashCalculations++;
                keccak = ValueKeccak.Compute(rlp.AsSpan());
                return true;
            }

            keccak = default;
            return false;
        }

        public Hash256? GenerateKey(ITrieNodeResolver tree, ref TreePath path,
            ICappedArrayPool? bufferPool = null, bool canBeParallel = true) =>
            TryGenerateKey(tree, ref path, out ValueHash256 keccak, bufferPool, canBeParallel)
                ? new Hash256(in keccak)
                : null;

        internal CappedArray<byte> RlpEncode(ITrieNodeResolver tree, ref TreePath path, ICappedArrayPool? bufferPool = null, bool canBeParallel = false)
        {
            return NodeType switch
            {
                NodeType.Branch => TrieNodeDecoder.RlpEncodeBranch(this, tree, ref path, bufferPool,
                    canBeParallel: canBeParallel),
                NodeType.Extension => TrieNodeDecoder.EncodeExtension(this, tree, ref path, bufferPool, canBeParallel),
                NodeType.Leaf => TrieNodeDecoder.EncodeLeaf(this, bufferPool),
                _ => ThrowUnhandledNodeType(this)
            };

            [DoesNotReturn, StackTraceHidden]
            static CappedArray<byte> ThrowUnhandledNodeType(TrieNode item) => throw new TrieException($"An attempt was made to encode a trie node of type {item.NodeType}");
        }

        public Hash256? GetChildHash(int i)
        {
            CappedArray<byte> rlp = ReadRlp();
            if (rlp.IsNull)
            {
                return null;
            }

            ValueRlpStream rlpStream = new(rlp);
            SeekChild(ref rlpStream, i);
            (int _, int length) = rlpStream.PeekPrefixAndContentLength();
            return length == 32 ? rlpStream.DecodeKeccak() : null;
        }

        /// Gets child hash or the Value of the node in case it's an inline.
        public byte[]? GetChildHashOrInlineValue(int i)
        {
            CappedArray<byte> rlp = ReadRlp();
            if (rlp.IsNull)
            {
                return null;
            }

            ValueRlpStream rlpStream = new(rlp);
            SeekChild(ref rlpStream, i);

            int prefix = rlpStream.PeekByte();

            // If it's a hash (32 bytes), decode and return it
            if (prefix == 160)
            {
                return rlpStream.DecodeKeccak().Bytes.ToArray();
            }

            int prefixValue = rlpStream.PeekByte();
            if (prefixValue < 192)
            {
                return null;
            }
            else
            {
                // If it's an RLP list (inline node), return the full item as a byte array
                rlpStream.PeekNextItem();
                rlpStream.SkipLength();
                rlpStream.SkipItem();
                return rlpStream.DecodeByteArraySpan().ToArray();
            }
        }

        public byte[]? GetInlineNodeRlp(int i)
        {
            CappedArray<byte> rlp = ReadRlp();
            if (rlp.IsNull)
            {
                return null;
            }

            ValueRlpStream rlpStream = new(rlp);
            SeekChild(ref rlpStream, i);

            int prefixValue = rlpStream.PeekByte();
            if (prefixValue < 192)
            {
                return null;
            }
            else
            {
                int length = rlpStream.PeekNextRlpLength();
                return rlpStream.Read(length).ToArray();
            }
        }

        public bool GetChildHashAsValueKeccak(int i, out ValueHash256 keccak)
        {
            Unsafe.SkipInit(out keccak);
            CappedArray<byte> rlp = ReadRlp();
            if (rlp.IsNull)
            {
                return false;
            }

            ValueRlpStream rlpStream = new(rlp);
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

            CappedArray<byte> rlp = ReadRlp();
            ref TrieNode? data = ref GetSlotRef(i);
            TrieNode? observed = Volatile.Read(ref data);
            if (rlp.IsNotNull && observed is null)
            {
                ValueRlpStream rlpStream = new(rlp);
                SeekChild(ref rlpStream, i);
                return rlpStream.PeekNextRlpLength() == 1;
            }

            return observed is null || ReferenceEquals(observed, NullNode);

            [DoesNotReturn, StackTraceHidden]
            static void ThrowNotABranch() => throw new TrieException(
                    "An attempt was made to ask about whether a child is null on a non-branch node.");
        }

        public bool TryGetDirtyChild(int i, [NotNullWhen(true)] out TrieNode? dirtyChild)
        {
            if (IsExtension)
            {
                i++;
            }

            ref TrieNode? data = ref GetSlotRef(i);
            TrieNode? observed = Volatile.Read(ref data);
            if (observed is null || ReferenceEquals(observed, NullNode))
            {
                dirtyChild = null;
                return false;
            }

            dirtyChild = observed;
            return dirtyChild.IsDirty;
        }

        /// <summary>
        /// Returns the raw stored reference for child slot <paramref name="childIndex"/>: a
        /// <see cref="TrieNode"/> for resolved children, the null sentinel for known-empty
        /// slots, or <see langword="null"/> for slots that have not been decoded from the
        /// parent RLP yet.
        /// </summary>
        /// <remarks>
        /// Boundary-stitching code (snap-sync proof healing) needs to distinguish
        /// resolved / unresolved / empty slot states without paying for a hash decode.
        /// For unresolved slots the canonical hash lives in the parent's retained
        /// <c>_rlpArray</c>; use <see cref="TryGetChildHash(int, out ValueHash256)"/> to
        /// pull it on demand. Extensions store the child reference at slot 0.
        /// </remarks>
        public TrieNode? GetRawChildRef(int childIndex) =>
            this is TrieNodeBranch or TrieNodeExtension ? GetSlotRef(childIndex) : null;

        /// <summary>
        /// Read the child reference at <paramref name="childIndex"/> directly from the
        /// retained parent RLP without resolving it. Returns <c>true</c> with
        /// <paramref name="hash"/> set when the slot encodes a 32-byte by-hash reference;
        /// returns <c>false</c> for empty slots, inline children, or when the parent has
        /// no RLP. The child slot itself is not touched.
        /// </summary>
        public bool TryGetChildHash(int childIndex, out ValueHash256 hash)
        {
            CappedArray<byte> rlp = ReadRlp();
            if (rlp.IsNull)
            {
                hash = default;
                return false;
            }

            ValueRlpStream rlpStream = new(rlp);
            SeekChild(ref rlpStream, childIndex);
            (_, int length) = rlpStream.PeekPrefixAndContentLength();
            if (length == 32 && rlpStream.DecodeValueKeccak(out hash))
            {
                return true;
            }

            hash = default;
            return false;
        }

        /// <summary>
        /// Drops the child slot at <paramref name="childIndex"/> back to the unresolved
        /// state. The canonical hash lives in the parent's retained <c>_rlpArray</c>;
        /// the next read will decode it on demand. Used by snap-sync boundary stitching
        /// to release the typed child once it falls outside the proven range.
        /// Asserts the node is a branch.
        /// </summary>
        public void SetUnresolvedChildHashAt(int childIndex, in ValueHash256 hash)
        {
            if (!IsBranch) ThrowNotABranch();
            // The hash already lives in the parent RLP at this slot; clearing the
            // resolved reference is enough — the next read decodes it on demand.
            // The hash argument is preserved for caller introspection / future asserts.
            _ = hash;
            Volatile.Write(ref GetSlotRef(childIndex), null);

            [DoesNotReturn, StackTraceHidden]
            static void ThrowNotABranch() => throw new TrieException(
                $"{nameof(SetUnresolvedChildHashAt)} called on a non-branch node.");
        }

        public TrieNode? this[int i]
        {
            set { SetChild(i, value); }
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

        public TrieNode? GetChildWithChildPath(ITrieNodeResolver tree, ref TreePath childPath, int childIndex, bool keepChildRef = false)
        {
            /* extensions store value before the child while branches store children before the value
             * so just to treat them in the same way we update index on extensions
             */
            childIndex = IsExtension ? childIndex + 1 : childIndex;
            TrieNode? childOrRef = ResolveChildWithChildPath(tree, ref childPath, childIndex);

            TrieNode? child;
            if (childOrRef is null || ReferenceEquals(childOrRef, NullNode))
            {
                child = null;
            }
            else
            {
                child = childOrRef;
            }

            // pruning trick so we never store long persisted paths
            // Don't unresolve nodes with path length <= 4; there should be relatively few and they should fit
            // in RAM, but they are hit quite a lot, and don't have very good data locality.
            // That said, in practice, it does nothing notable, except for significantly improving benchmark score.
            if (child?.IsPersisted == true && !keepChildRef && childPath.Length > 4 && childPath.Length % 2 == 0)
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

            SetItem(i, child);
        }

        public void SetChild(int i, TrieNode? node)
        {
            if (IsSealed)
            {
                ThrowAlreadySealed();
            }

            SetItem(i, node);
            ClearKeccak();

            [DoesNotReturn, StackTraceHidden]
            void ThrowAlreadySealed() => throw new InvalidOperationException(
                    $"{nameof(TrieNode)} {this} is already sealed when setting a child.");
        }

        /// <summary>
        /// Seed branch slot <paramref name="i"/> with an unresolved-by-hash child
        /// reference. Test-only: encodes <paramref name="hash"/> into a typed leaf
        /// placeholder so the branch encoder writes the same bytes a freshly-loaded
        /// branch would, without going through the resolver. Production code does not
        /// touch this — branches are constructed from RLP and read child hashes from
        /// their retained <c>_rlpArray</c>.
        /// </summary>
        internal void SetChildHash(int i, Hash256 hash)
        {
            ArgumentNullException.ThrowIfNull(hash);
            if (IsSealed)
            {
                ThrowAlreadySealed();
            }

            // The slot now holds typed TrieNode references only; encode the hash via
            // a typed placeholder so the branch encoder produces the same RLP a
            // freshly-loaded child would. Mark persisted so the encoder treats it as
            // a known-by-hash child rather than a dirty leaf to re-encode.
            TrieNodeLeaf placeholder = new(in hash.ValueHash256);
            placeholder.IsPersisted = true;
            Volatile.Write(ref GetSlotRef(i), placeholder);
            ClearKeccak();

            [DoesNotReturn, StackTraceHidden]
            void ThrowAlreadySealed() => throw new InvalidOperationException(
                    $"{nameof(TrieNode)} {this} is already sealed when setting a child.");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SetItem(int i, TrieNode? node)
        {
            int index = IsExtension ? i + 1 : i;
            GetSlotRef(i) = node ?? NullNode;
        }

        public long GetMemorySize(bool recursive)
        {
            // Inline ValueHash256 (32 B) + uint seqlock counter (4 B, in alignment padding); no separate Hash256 object.
            int keccakSize = ValueHash256.MemorySize + sizeof(uint);
            CappedArray<byte> rlp = ReadRlp();
            bool isRlpSlice = IsRlpSlice(Volatile.Read(ref _rlpSeqAndLength));
            long rlpSize = MemorySizes.RefSize + (rlp.IsNotNull && !isRlpSlice ? MemorySizes.ArrayOverhead + rlp.UnderlyingLength : 0);
            // B4: shape data lives inline on the typed subclass; no longer a separate INodeData heap object.
            long dataSize = MemorySizeOfData;
            int objectOverhead = MemorySizes.ObjectHeaderMethodTable;
            int blockAndFlagsSize = sizeof(long);

            if (this is TrieNodeBranch branchNode)
            {
                for (int i = 0; i < BranchArray.Length; i++)
                {
                    TrieNode? child = branchNode._branches[i];
                    if (child is null || ReferenceEquals(child, NullNode))
                    {
                        continue;
                    }

                    dataSize += recursive ? child.GetMemorySize(true) : 0;
                }
            }
            else if (this is TrieNodeExtension extensionNode)
            {
                TrieNode? child = extensionNode._child;
                if (child is not null && !ReferenceEquals(child, NullNode))
                {
                    dataSize += recursive ? child.GetMemorySize(true) : 0;
                }
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
            TrieNode trieNode = CloneTyped();

            CappedArray<byte> rlp = ReadRlp();
            if (rlp.IsNotNull)
            {
                trieNode.InitRlp(rlp);
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

            if (this is TrieNodeBranch branchNode)
            {
                ref BranchArray data = ref branchNode._branches;
                int previousLength = AppendChildPath(ref currentPath, 0);
                for (int i = 0; i < BranchArray.Length; i++)
                {
                    TrieNode? child = data[i];
                    if (child is null || ReferenceEquals(child, NullNode)) continue;
                    if (logger.IsTrace) logger.Trace($"Persist recursively on child {i} {child} of {this}");
                    currentPath.SetLast(i);
                    child.CallRecursively(action, storageAddress, ref currentPath, resolver, skipPersisted, logger,
                        maxPathLength, resolveStorageRoot);
                }

                currentPath.TruncateMut(previousLength);
            }
            else if (this is TrieNodeExtension extensionNode)
            {
                TrieNode? child = extensionNode._child;
                if (child is not null && !ReferenceEquals(child, NullNode))
                {
                    if (logger.IsTrace) logger.Trace($"Persist recursively on child 0 {child} of {this}");
                    int previousLength = AppendChildPath(ref currentPath, 0);
                    child.CallRecursively(action, storageAddress, ref currentPath, resolver, skipPersisted, logger,
                        maxPathLength, resolveStorageRoot);
                    currentPath.TruncateMut(previousLength);
                }
            }
            else if (this is TrieNodeLeaf leafNode)
            {
                TrieNode? storageRoot = leafNode._storageRoot;
                if (resolveStorageRoot && (storageRoot is not null ||
                                           TryResolveStorageRoot(resolver, ref currentPath, out storageRoot)))
                {
                    if (logger.IsTrace)
                        logger.Trace($"Persist recursively on storage root {leafNode._storageRoot} of {this}");
                    Hash256 storagePathAddr;
                    using (currentPath.ScopedAppend(Key))
                    {
                        if (currentPath.Length != 64)
                            throw new TrieException(
                                $"unexpected storage path length. Total nibble count should add up to 64. Got {currentPath.Length}.");
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

            if (this is not TrieNodeLeaf leafNode)
            {
                // Unknown placeholders have no children to traverse; just call action.
                if (this is not (TrieNodeBranch or TrieNodeExtension))
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
                    leafNode,
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
            if (this is TrieNodeBranch branchNode)
            {
                for (int i = 0; i < BranchArray.Length; i++)
                {
                    TrieNode? child = branchNode._branches[i];
                    if (child is null || ReferenceEquals(child, NullNode)) continue;
                    if (logger.IsTrace) logger.Trace($"Persist recursively on child {i} {child} of {this}");
                    int previousLength = AppendChildPath(ref currentPath, i);
                    await child.CallRecursivelyAsync(action, storageAddress, ref currentPath, resolver, logger);
                    currentPath.TruncateMut(previousLength);
                }
            }
            else if (this is TrieNodeExtension extensionNode)
            {
                TrieNode? child = extensionNode._child;
                if (child is not null && !ReferenceEquals(child, NullNode))
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
            TrieNodeLeaf leafNode,
            ILogger logger)
        {
            TrieNode? storageRoot = leafNode._storageRoot;
            if (storageRoot is not null || TryResolveStorageRoot(resolver, ref currentPath, out storageRoot))
            {
                if (logger.IsTrace) logger.Trace($"Persist recursively on storage root {storageRoot} of {this}");
                Hash256 storagePathAddr;
                using (currentPath.ScopedAppend(Key))
                {
                    if (currentPath.Length != 64)
                        throw new TrieException("unexpected storage path length. Total nibble count should add up to 64.");
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
            if (this is not TrieNodeLeaf leafNode)
            {
                if (this is TrieNodeBranch branchNode)
                {
                    ref BranchArray data = ref branchNode._branches;
                    for (int i = 0; i < BranchArray.Length; i++)
                    {
                        TrieNode? child = data[i];
                        if (child is null || ReferenceEquals(child, NullNode)) continue;
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
                else if (this is TrieNodeExtension extensionNode)
                {
                    TrieNode? child = extensionNode._child;
                    if (child is not null && !ReferenceEquals(child, NullNode))
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
            else
            {
                if (leafNode._storageRoot?.IsPersisted == true)
                {
                    leafNode._storageRoot = null;
                }
            }

            // else
            // {
            //     // we assume that the storage root will get resolved during persistence even if not persisted yet
            //     // if this is not true then the code above that is commented out would be critical to call instead
            //     _storageRoot = null;
            // }
        }

        internal bool TryResolveStorageRoot(ITrieNodeResolver resolver, ref TreePath currentPath,
            out TrieNode? storageRoot)
        {
            bool hasStorage = false;

            if (this is TrieNodeLeaf leafNode)
            {
                storageRoot = leafNode._storageRoot;
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

                        // Eagerly load + decode the storage root so the returned node is a typed
                        // TrieNodeBranch/Leaf/Extension instead of a NodeType.Unknown placeholder.
                        // Every caller of TryResolveStorageRoot (visitor, persist walks) immediately
                        // walks into the storage trie, so the historical laziness never bought
                        // anything in practice; GetOrLoadNode's default already hits the cache
                        // first via TryGetCachedNode. Use the Try-shape so an unresolvable storage
                        // root (e.g. partial sync, test fixtures) surfaces as hasStorage=false and
                        // the visitor sees VisitMissingNode instead of a thrown TrieException
                        // escaping past the per-node try/catch boundary in TrieNodeTraverser.Accept.
                        if (resolver.GetStorageTrieNodeResolver(storagePath)
                                .TryGetOrLoadNode(in TreePath.Empty, storageRootKey, out TrieNode? loaded))
                        {
                            hasStorage = true;
                            leafNode._storageRoot = storageRoot = loaded;
                        }
                    }
                }
            }
            else
            {
                storageRoot = null;
            }

            return hasStorage;
        }

        private void SeekChild(ref ValueRlpStream rlpStream, int index)
        {
            if (rlpStream.IsNull)
            {
                return;
            }

            SeekChildNotNull(ref rlpStream, index);
        }

        private void SeekChildNotNull(ref ValueRlpStream rlpStream, int index)
        {
            rlpStream.Reset();
            rlpStream.SkipLength();
            if (index == 0 && IsExtension)
            {
                // Corner case, index is zero, but we are an extension
                // so we need to move to next item
                index = 1;
            }

            for (int i = 0; i < index; i++)
            {
                rlpStream.SkipItem();
            }
        }

        // Slot publication primitive paired with Volatile.Read on child-slot readers.
        // On x64 the read is a plain mov and the CAS is a locked op; on ARM64 the read is ldar
        // (load-acquire) and the CAS provides store-release - readers see fully-constructed children.
        private static TrieNode PublishChild(ref TrieNode? slot, TrieNode decoded)
        {
            TrieNode? winner = Interlocked.CompareExchange(ref slot, decoded, null);
            return winner ?? decoded;
        }

        /// <summary>
        /// Decode the child at the current <paramref name="rlpStream"/> position. Empty
        /// entries return <see cref="NullNode"/>; inline children decode eagerly into a
        /// typed instance; by-hash references load through <paramref name="tree"/>.
        /// <see cref="ITrieNodeResolver.GetOrLoadNode"/>, returning a fully resolved typed
        /// <see cref="TrieNode"/>. The hash itself is not retained in the slot — it lives
        /// in the parent's <c>_rlpArray</c>.
        /// </summary>
        private static TrieNode DecodeChildReference(ITrieNodeResolver tree, in TreePath childPath, CappedArray<byte> rlp, ref ValueRlpStream rlpStream)
        {
            int prefix = rlpStream.ReadByte();
            switch (prefix)
            {
                case 0:
                case 128:
                    return NullNode;
                case 160:
                    rlpStream.Position--;
                    rlpStream.DecodeValueKeccak(out ValueHash256 childHash);
                    return tree.GetOrLoadNode(in childPath, in childHash);
                default:
                    rlpStream.Position--;
                    int offset = rlpStream.Position;
                    int length = rlpStream.PeekNextRlpLength();
                    // Inline child decode is eager: with the parent RLP already in hand
                    // there is no IO and we never publish a placeholder shape.
                    TrieNode child = CreateInlineChild(in childPath, rlp, offset, length);
                    rlpStream.SkipBytes(length);
                    return child;
            }
        }

        private TrieNode? ResolveChildWithChildPath(ITrieNodeResolver tree, ref TreePath childPath, int i)
        {
            TrieNode? childOrRef;
            CappedArray<byte> rlp = ReadRlp();
            ref TrieNode? data = ref GetSlotRef(i);
            if (rlp.IsNull)
            {
                childOrRef = Volatile.Read(ref data);
            }
            else
            {
                childOrRef = Volatile.Read(ref data);
                if (childOrRef is null)
                {
                    // Lazy resolve from parent RLP: empty entries publish NullNode,
                    // inline children publish the typed inline TrieNode, by-hash
                    // references resolve through the supplied resolver and publish the
                    // fully resolved typed node.
                    ValueRlpStream rlpStream = new(rlp);
                    SeekChild(ref rlpStream, i);
                    childOrRef = DecodeChildReference(tree, in childPath, rlp, ref rlpStream);
                    childOrRef = PublishChild(ref data, childOrRef);
                }
            }

            return childOrRef;
        }

        /// <summary>
        /// Visitor fast path for branch nodes. Decodes child references directly from
        /// the parent RLP without touching <see cref="GetSlotRef(int)"/> slots: empty
        /// entries land as <c>null</c>, inline children decode to a typed inline node,
        /// by-hash references publish a hash-only carrier (<see cref="ChildHash"/>)
        /// so the visitor can issue <see cref="ITreeVisitor{T}.ShouldVisit"/> against
        /// the keccak without paying for a load it may end up skipping. Visitors that
        /// decide to recurse call <see cref="ITrieNodeResolver.GetOrLoadNode"/> with
        /// the child's hash.
        /// </summary>
        internal int ResolveAllChildBranch(ITrieNodeResolver tree, ref TreePath path, Span<ChildHash> output)
        {
            int chCount = 0;
            CappedArray<byte> rlp = ReadRlp();
            if (rlp.IsNull)
            {
                // No parent RLP: child slots are the canonical state. For each non-empty
                // slot publish a fully resolved typed child via the slot itself.
                path.AppendMut(0);
                for (int i = 0; i < 16; i++)
                {
                    path.SetLast(i);
                    TrieNode? n = GetChildWithChildPath(tree, ref path, i);
                    if (n is not null)
                    {
                        chCount++;
                        output[i] = new ChildHash(n);
                    }
                    else
                    {
                        output[i] = default;
                    }
                }

                path.TruncateOne();
                return chCount;
            }

            ValueRlpStream rlpStream = new(rlp);
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
                            output[i] = default;
                            break;
                        }
                    case 160:
                        {
                            path.SetLast(i);
                            rlpStream.DecodeValueKeccak(out ValueHash256 childHash);
                            chCount++;
                            output[i] = new ChildHash(in childHash);
                            break;
                        }
                    default:
                        {
                            path.SetLast(i);
                            int offset = rlpStream.Position;
                            int length = rlpStream.PeekNextRlpLength();
                            TrieNode child = CreateInlineChild(in path, rlp, offset, length);
                            rlpStream.SkipBytes(length);
                            chCount++;
                            output[i] = new ChildHash(child);
                            break;
                        }
                }
            }

            path.TruncateOne();

            return chCount;
        }

        /// <summary>
        /// Lightweight carrier returned by <see cref="ResolveAllChildBranch"/>. Holds
        /// either a fully resolved <see cref="TrieNode"/> (inline child or non-RLP
        /// branch) or just a <see cref="ValueHash256"/> for by-hash references that
        /// the visitor may yet decide to skip. <see cref="IsEmpty"/> distinguishes
        /// unset slots from non-empty children. Inline children carry no separate
        /// keccak; in that case <see cref="TryGetHash"/> returns <c>false</c> but
        /// <see cref="Node"/> still produces the typed inline node directly.
        /// </summary>
        public readonly struct ChildHash
        {
            private readonly TrieNode? _node;
            private readonly ValueHash256 _hash;
            private readonly bool _hasHash;

            public ChildHash(TrieNode node)
            {
                _node = node;
                _hash = default;
                _hasHash = false;
            }

            public ChildHash(in ValueHash256 hash)
            {
                _node = null;
                _hash = hash;
                _hasHash = true;
            }

            public bool IsEmpty => _node is null && !_hasHash;

            public TrieNode? Node => _node;

            public bool TryGetHash(out ValueHash256 hash)
            {
                if (_hasHash) { hash = _hash; return true; }
                if (_node is not null && _node.TryGetKeccak(out hash)) return true;
                hash = default;
                return false;
            }
        }

        internal void UnresolveChild(int i)
        {
            ref TrieNode? data = ref GetSlotRef(i);
            if (IsPersisted)
            {
                // Persisted parent: drop the slot to null. Next read decodes the
                // by-hash reference straight from the parent's retained _rlpArray.
                TrieNode? observed = Volatile.Read(ref data);
                if (observed is not null)
                {
                    Interlocked.CompareExchange(ref data, null, observed);
                }
            }
            else
            {
                // Non-persisted parent: only safe to free the typed reference if the
                // parent has RLP retaining the child hash; otherwise we'd lose the
                // link irretrievably. Skip the free in that case.
                TrieNode? observed = Volatile.Read(ref data);
                if (observed is null) return;
                if (!observed.IsPersisted)
                {
                    ThrowNotPersisted();
                    return;
                }

                if (HasRlp)
                {
                    Interlocked.CompareExchange(ref data, null, observed);
                }
            }

            [DoesNotReturn, StackTraceHidden]
            static void ThrowNotPersisted() => throw new InvalidOperationException("Cannot unresolve a child that is not persisted yet.");
        }

        public ChildIterator CreateChildIterator() => new(this);

        // Allow faster forward child iteration by not re-skipping items on each child seek
        public ref struct ChildIterator(TrieNode node)
        {
            private ValueRlpStream _rlpStream;
            private int? _currentStreamIndex;

            private TrieNode? ResolveChildWithChildPath(ITrieNodeResolver tree, ref TreePath childPath, int i)
            {
                TrieNode? childOrRef;
                CappedArray<byte> rlp = node.ReadRlp();
                ref TrieNode? data = ref node.GetSlotRef(i);
                if (rlp.IsNull)
                {
                    childOrRef = Volatile.Read(ref data);
                }
                else
                {
                    childOrRef = Volatile.Read(ref data);
                    if (childOrRef is null)
                    {
                        if (_currentStreamIndex.HasValue && _currentStreamIndex <= i)
                        {
                            int toSkip = i - _currentStreamIndex.Value;
                            for (int j = 0; j < toSkip; j++) _rlpStream.SkipItem();
                            _currentStreamIndex += toSkip;
                        }
                        else
                        {
                            _rlpStream = new ValueRlpStream(rlp);
                            _rlpStream.Reset();
                            _rlpStream.SkipLength();
                            if (node.IsExtension)
                            {
                                _rlpStream.SkipItem();
                                i--;
                            }
                            else
                            {
                                for (int j = 0; j < i; j++) _rlpStream.SkipItem();
                            }

                            _currentStreamIndex = i;
                        }

                        childOrRef = DecodeChildReference(tree, in childPath, rlp, ref _rlpStream);
                        childOrRef = PublishChild(ref data, childOrRef);
                        _currentStreamIndex++;
                    }
                }

                return childOrRef;
            }

            public TrieNode? GetChildWithChildPath(ITrieNodeResolver tree, ref TreePath childPath, int childIndex)
            {
                /* extensions store value before the child while branches store children before the value
                 * so just to treat them in the same way we update index on extensions
                 */
                childIndex = node.IsExtension ? childIndex + 1 : childIndex;
                TrieNode? childOrRef = ResolveChildWithChildPath(tree, ref childPath, childIndex);

                TrieNode? child = (childOrRef is null || ReferenceEquals(childOrRef, NullNode))
                    ? null
                    : childOrRef;

                // pruning trick so we never store long persisted paths
                // Don't unresolve nodes with path length <= 4; there should be relatively few and they should fit
                // in RAM, but they are hit quite a lot, and don't have very good data locality.
                // That said, in practice, it does nothing notable, except for significantly improving benchmark score.
                if (child?.IsPersisted == true && childPath.Length > 4 && childPath.Length % 2 == 0)
                {
                    node.UnresolveChild(childIndex);
                }

                return child;
            }
        }
    }
}
