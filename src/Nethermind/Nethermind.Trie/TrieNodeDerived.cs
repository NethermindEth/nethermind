// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie
{
    // B4 endpoint: branch / leaf / extension state now lives directly on the
    // derived sealed types. Base TrieNode no longer carries _nodeData; bare
    // TrieNode instances are placeholder Unknown nodes produced by legacy
    // FindCachedOrUnknown contracts (TrieStore / SnapshotBundle / CommitSetQueue).
    // Once every resolver in the tree exposes typed load/decode APIs (and only
    // when), TrieNode can become abstract; that work is out of scope here.

    /// <summary>16-slot inline storage for branch children. Each slot holds one of:
    /// <see langword="null"/> (unresolved - decode from parent RLP on read),
    /// the empty sentinel (<see cref="TrieNode.NullNode"/>), or a resolved
    /// <see cref="TrieNode"/>. The unresolved-by-hash shape is gone:
    /// hashes live in the parent's retained <c>_rlpArray</c> only.</summary>
    [InlineArray(Length)]
    internal struct BranchArray
    {
        public const int Length = TrieNode.BranchesCount;
        private TrieNode? _element0;
    }

    /// <summary>
    /// Empty-child marker. A distinct sentinel <see cref="TrieNode"/> instance the
    /// branch / extension slot publishes when the parent RLP encoded an empty entry,
    /// so subsequent reads do not re-decode the empty marker. Compared by reference.
    /// </summary>
    internal sealed class TrieNodeNullSentinel : TrieNode
    {
        internal TrieNodeNullSentinel() { }
        public override NodeType NodeType => NodeType.Unknown;
        internal override TrieNode CloneTyped() => this;
    }

    /// <summary>
    /// Concrete sealed placeholder for an as-yet-unresolved by-hash trie node. Carries
    /// only an optional <see cref="ValueHash256"/> identity and / or RLP payload; the
    /// shape (branch / leaf / extension) is determined later by <see cref="TrieNode.ResolveNode"/>,
    /// which decodes the RLP into a typed derived class and rebinds the caller's reference.
    /// Exists so the abstract <see cref="TrieNode"/> base does not need a public
    /// constructor for the unknown case while resolver / cache / sync layers that still
    /// publish unresolved node identities have a concrete type to allocate.
    /// </summary>
    public sealed class TrieNodePlaceholder : TrieNode
    {
        public TrieNodePlaceholder() { }

        public TrieNodePlaceholder(in ValueHash256 keccak) : base(in keccak) => IsPersisted = true;

        public TrieNodePlaceholder(Hash256 keccak) : base(in (keccak ?? throw new ArgumentNullException(nameof(keccak))).ValueHash256) => IsPersisted = true;

        public TrieNodePlaceholder(CappedArray<byte> rlp, bool isDirty = false) : base(rlp, isDirty) { }

        public TrieNodePlaceholder(byte[]? rlp, bool isDirty = false)
            : base(new CappedArray<byte>(rlp), isDirty) { }

        public TrieNodePlaceholder(Hash256 keccak, ReadOnlySpan<byte> rlp)
            : base(new CappedArray<byte>(rlp.ToArray()), in (keccak ?? throw new ArgumentNullException(nameof(keccak))).ValueHash256) => IsPersisted = true;

        public TrieNodePlaceholder(Hash256 keccak, CappedArray<byte> rlp)
            : base(rlp, in (keccak ?? throw new ArgumentNullException(nameof(keccak))).ValueHash256) => IsPersisted = true;

        public TrieNodePlaceholder(in ValueHash256 keccak, CappedArray<byte> rlp)
            : base(rlp, in keccak) => IsPersisted = true;

        // Copy ctor for CloneTyped: preserves the unknown shape; the caller is expected
        // to ResolveNode the clone before treating it as a typed node.
        private TrieNodePlaceholder(TrieNodePlaceholder source) : base(source) { }

        internal override TrieNode CloneTyped() => new TrieNodePlaceholder(this);

        public override NodeType NodeType => NodeType.Unknown;
    }

    internal sealed class TrieNodeBranch : TrieNode
    {
        internal BranchArray _branches;

        internal TrieNodeBranch() { }
        internal TrieNodeBranch(in ValueHash256 keccak) : base(in keccak) { }
        internal TrieNodeBranch(CappedArray<byte> rlp, bool isDirty) : base(rlp, isDirty) { }
        internal TrieNodeBranch(in ValueHash256 keccak, CappedArray<byte> rlp)
            : base(rlp, in keccak) { }

        // Clone constructor: copy each branch slot and the dirty mask.
        private TrieNodeBranch(TrieNodeBranch source) : base(source)
        {
            // Shallow copy of the 16 slots — TrieNode references are shared
            for (int i = 0; i < BranchArray.Length; i++)
            {
                _branches[i] = source._branches[i];
            }
        }

        internal override TrieNode CloneTyped() => new TrieNodeBranch(this);

        public override NodeType NodeType => NodeType.Branch;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override ref TrieNode? GetSlotRef(int index) => ref _branches[index];

        internal override int MemorySizeOfData =>
            MemorySizes.RefSize * BranchArray.Length;
    }

    internal sealed class TrieNodeLeaf : TrieNode
    {
        internal byte[]? _key;
        internal CappedArray<byte> _value;
        internal TrieNode? _storageRoot;

        internal TrieNodeLeaf() => _value = CappedArray<byte>.Empty;

        internal TrieNodeLeaf(byte[] key, CappedArray<byte> value)
        {
            _key = key;
            _value = value;
        }

        internal TrieNodeLeaf(in ValueHash256 keccak) : base(in keccak) => _value = CappedArray<byte>.Empty;

        internal TrieNodeLeaf(CappedArray<byte> rlp, bool isDirty) : base(rlp, isDirty) => _value = CappedArray<byte>.Empty;

        internal TrieNodeLeaf(in ValueHash256 keccak, CappedArray<byte> rlp)
            : base(rlp, in keccak) => _value = CappedArray<byte>.Empty;

        private TrieNodeLeaf(TrieNodeLeaf source) : base(source)
        {
            _key = source._key;
            _value = source._value;
            _storageRoot = source._storageRoot;
        }

        internal override TrieNode CloneTyped() => new TrieNodeLeaf(this);

        public override NodeType NodeType => NodeType.Leaf;

        internal override byte[]? KeyInternal
        {
            get => _key;
            set => _key = value;
        }

        internal override ref CappedArray<byte> ValueRef => ref _value;

        internal override ref TrieNode? StorageRootRef => ref _storageRoot;

        // Leaf has no indexed child slots; slot 0 is the value-only Path used by some
        // decoders that walk extension/leaf children uniformly. Leaf RLP encoding uses
        // Key + Value, not slot-based child decoding, so callers should never invoke
        // GetSlotRef on a leaf. Throw to surface accidental misuse.
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal override ref TrieNode? GetSlotRef(int index) => throw new IndexOutOfRangeException();

        internal override int MemorySizeOfData =>
            MemorySizes.RefSize + // storage root reference
            MemorySizes.RefSize + // key reference
            (_key is not null ? (int)MemorySizes.Align(_key.Length + MemorySizes.ArrayOverhead) : 0) +
            MemorySizes.RefSize + // value array reference
            (_value.IsNotNullOrEmpty ? (int)MemorySizes.Align(_value.UnderlyingLength + MemorySizes.ArrayOverhead) : 0);

        internal TrieNodeLeaf CloneWithNewValue(CappedArray<byte> value)
        {
            TrieNodeLeaf clone = new(_key!, value);
            clone._storageRoot = _storageRoot;
            return clone;
        }
    }

    internal sealed class TrieNodeExtension : TrieNode
    {
        internal byte[]? _key;
        internal TrieNode? _child;

        internal TrieNodeExtension() { }

        internal TrieNodeExtension(byte[] key) => _key = key;

        internal TrieNodeExtension(byte[] key, TrieNode child)
        {
            _key = key;
            _child = child;
        }

        internal TrieNodeExtension(in ValueHash256 keccak) : base(in keccak) { }

        internal TrieNodeExtension(CappedArray<byte> rlp, bool isDirty) : base(rlp, isDirty) { }

        internal TrieNodeExtension(in ValueHash256 keccak, CappedArray<byte> rlp)
            : base(rlp, in keccak) { }

        private TrieNodeExtension(TrieNodeExtension source) : base(source)
        {
            _key = source._key;
            _child = source._child;
        }

        internal override TrieNode CloneTyped() => new TrieNodeExtension(source: this);

        public override NodeType NodeType => NodeType.Extension;

        internal override byte[]? KeyInternal
        {
            get => _key;
            set => _key = value;
        }

        // Extension stores the child reference at slot 0. Index is ignored — callers
        // pass 0 when handling extensions explicitly and (i + 1) when iterating uniformly
        // with branches (i is always 0 in that case).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override ref TrieNode? GetSlotRef(int index) => ref _child;

        internal override int MemorySizeOfData =>
            MemorySizes.RefSize + // child reference
            MemorySizes.RefSize + // key reference
            (_key is not null ? (int)MemorySizes.Align(_key.Length + MemorySizes.ArrayOverhead) : 0);
    }

    public partial class TrieNode
    {
        /// <summary>
        /// Allocate a typed dirty branch node.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static TrieNode CreateBranchTyped() => new TrieNodeBranch();

        /// <summary>
        /// Allocate a typed sealed branch node carrying a known Keccak.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static TrieNode CreateBranchTyped(Hash256 keccak)
        {
            ArgumentNullException.ThrowIfNull(keccak);
            return new TrieNodeBranch(in keccak.ValueHash256);
        }

        /// <summary>
        /// Allocate a typed branch node initialized from RLP.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static TrieNode CreateBranchTyped(byte[]? rlp, bool isDirty = false)
            => new TrieNodeBranch(new CappedArray<byte>(rlp), isDirty);

        /// <summary>
        /// Allocate a typed branch node initialized from RLP wrapped in a CappedArray.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static TrieNode CreateBranchTyped(CappedArray<byte> rlp, bool isDirty = false)
            => new TrieNodeBranch(rlp, isDirty);

        /// <summary>
        /// Allocate a typed leaf node from an already hex-prefixed key and a value.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static TrieNode CreateLeafTyped(byte[] hexPrefixedKey, CappedArray<byte> value)
            => new TrieNodeLeaf(hexPrefixedKey, value);

        /// <summary>
        /// Allocate a typed empty leaf node. The key and value are filled in by the caller.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static TrieNode CreateLeafTyped() => new TrieNodeLeaf();

        /// <summary>
        /// Allocate a typed sealed leaf node carrying a known Keccak.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static TrieNode CreateLeafTyped(Hash256 keccak)
        {
            ArgumentNullException.ThrowIfNull(keccak);
            return new TrieNodeLeaf(in keccak.ValueHash256);
        }

        /// <summary>
        /// Allocate a typed leaf node initialized from RLP.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static TrieNode CreateLeafTyped(byte[]? rlp, bool isDirty = false)
            => new TrieNodeLeaf(new CappedArray<byte>(rlp), isDirty);

        /// <summary>
        /// Allocate a typed leaf node initialized from RLP wrapped in a CappedArray.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static TrieNode CreateLeafTyped(CappedArray<byte> rlp, bool isDirty = false)
            => new TrieNodeLeaf(rlp, isDirty);

        /// <summary>
        /// Allocate a typed leaf node carrying a known Keccak and the corresponding RLP.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static TrieNode CreateLeafTyped(Hash256 keccak, CappedArray<byte> rlp)
        {
            ArgumentNullException.ThrowIfNull(keccak);
            return new TrieNodeLeaf(in keccak.ValueHash256, rlp);
        }

        /// <summary>
        /// Allocate a typed leaf node carrying a known Keccak and the corresponding RLP bytes.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static TrieNode CreateLeafTyped(Hash256 keccak, ReadOnlySpan<byte> rlp)
        {
            ArgumentNullException.ThrowIfNull(keccak);
            return new TrieNodeLeaf(in keccak.ValueHash256, new CappedArray<byte>(rlp.ToArray()));
        }

        /// <summary>
        /// Allocate a typed extension node carrying an already hex-prefixed key.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static TrieNode CreateExtensionTyped(byte[] hexPrefixedKey)
            => new TrieNodeExtension(hexPrefixedKey);

        /// <summary>
        /// Allocate a typed extension node carrying an already hex-prefixed key
        /// and an in-memory child reference.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static TrieNode CreateExtensionTyped(byte[] hexPrefixedKey, TrieNode child)
            => new TrieNodeExtension(hexPrefixedKey, child);

        /// <summary>
        /// Allocate a typed empty extension node.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static TrieNode CreateExtensionTyped() => new TrieNodeExtension();

        /// <summary>
        /// Allocate a typed sealed extension node carrying a known Keccak.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static TrieNode CreateExtensionTyped(Hash256 keccak)
        {
            ArgumentNullException.ThrowIfNull(keccak);
            return new TrieNodeExtension(in keccak.ValueHash256);
        }

        /// <summary>
        /// Allocate a typed extension node initialized from RLP.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static TrieNode CreateExtensionTyped(byte[]? rlp, bool isDirty = false)
            => new TrieNodeExtension(new CappedArray<byte>(rlp), isDirty);

        /// <summary>
        /// Allocate a typed extension node initialized from RLP wrapped in a CappedArray.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static TrieNode CreateExtensionTyped(CappedArray<byte> rlp, bool isDirty = false)
            => new TrieNodeExtension(rlp, isDirty);
    }
}
