// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie
{
    /// <summary>
    /// 16-slot inline storage for branch children. Slots hold null (unresolved - decode from
    /// parent RLP on read), the empty sentinel <see cref="TrieNode.NullNode"/>, or a resolved
    /// <see cref="TrieNode"/>. Unresolved-by-hash slots live in the parent's retained RLP only.
    /// </summary>
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
    /// Transient typed-node wrapper for sync RLP. Shape is determined later by
    /// <see cref="TrieNode.ResolveNode"/> which decodes the RLP and rebinds the caller's ref.
    /// Sync-only: do not add a production hash-only ctor (the prior one falsely marked
    /// nodes IsPersisted before storage confirmation, polluting the cache).
    /// </summary>
    internal sealed class TrieSyncNode : TrieNode
    {
        internal TrieSyncNode() { }

        internal TrieSyncNode(CappedArray<byte> rlp, bool isDirty = false) : base(rlp, isDirty) { }

        internal TrieSyncNode(byte[]? rlp, bool isDirty = false)
            : base(new CappedArray<byte>(rlp), isDirty) { }

        // Test-only hash stub. Does not set IsPersisted.
        internal TrieSyncNode(in ValueHash256 keccak) : base(in keccak) { }

        internal TrieSyncNode(Hash256 keccak) : base(in keccak.ValueHash256) { }

        private TrieSyncNode(TrieSyncNode source) : base(source) { }

        internal override TrieNode CloneTyped() => new TrieSyncNode(this);

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

        private TrieNodeBranch(TrieNodeBranch source) : base(source)
        {
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

        // Leaves have no indexed child slots; callers should never invoke this.
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal override ref TrieNode? GetSlotRef(int index) => throw new IndexOutOfRangeException();

        internal override int MemorySizeOfData =>
            MemorySizes.RefSize + // storage root reference
            MemorySizes.RefSize + // key reference
            (_key is not null ? (int)MemorySizes.Align(_key.Length + MemorySizes.ArrayOverhead) : 0) +
            MemorySizes.RefSize + sizeof(int) * 2 + // CappedArray<byte>: value array reference + offset + length
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

        // Extension stores its single child at _child; index is ignored.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override ref TrieNode? GetSlotRef(int index) => ref _child;

        internal override int MemorySizeOfData =>
            MemorySizes.RefSize + // child reference
            MemorySizes.RefSize + // key reference
            (_key is not null ? (int)MemorySizes.Align(_key.Length + MemorySizes.ArrayOverhead) : 0);
    }

    public partial class TrieNode
    {
        // Branch factories.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static TrieNode CreateBranchTyped() => new TrieNodeBranch();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static TrieNode CreateBranchTyped(Hash256 keccak) => new TrieNodeBranch(in keccak.ValueHash256);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static TrieNode CreateBranchTyped(byte[]? rlp, bool isDirty = false)
            => new TrieNodeBranch(new CappedArray<byte>(rlp), isDirty);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static TrieNode CreateBranchTyped(CappedArray<byte> rlp, bool isDirty = false)
            => new TrieNodeBranch(rlp, isDirty);

        // Leaf factories.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static TrieNode CreateLeafTyped(byte[] hexPrefixedKey, CappedArray<byte> value)
            => new TrieNodeLeaf(hexPrefixedKey, value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static TrieNode CreateLeafTyped() => new TrieNodeLeaf();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static TrieNode CreateLeafTyped(Hash256 keccak) => new TrieNodeLeaf(in keccak.ValueHash256);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static TrieNode CreateLeafTyped(byte[]? rlp, bool isDirty = false)
            => new TrieNodeLeaf(new CappedArray<byte>(rlp), isDirty);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static TrieNode CreateLeafTyped(CappedArray<byte> rlp, bool isDirty = false)
            => new TrieNodeLeaf(rlp, isDirty);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static TrieNode CreateLeafTyped(Hash256 keccak, CappedArray<byte> rlp) => new TrieNodeLeaf(in keccak.ValueHash256, rlp);

        // Extension factories.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static TrieNode CreateExtensionTyped(byte[] hexPrefixedKey)
            => new TrieNodeExtension(hexPrefixedKey);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static TrieNode CreateExtensionTyped(byte[] hexPrefixedKey, TrieNode child)
            => new TrieNodeExtension(hexPrefixedKey, child);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static TrieNode CreateExtensionTyped() => new TrieNodeExtension();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static TrieNode CreateExtensionTyped(Hash256 keccak) => new TrieNodeExtension(in keccak.ValueHash256);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static TrieNode CreateExtensionTyped(byte[]? rlp, bool isDirty = false)
            => new TrieNodeExtension(new CappedArray<byte>(rlp), isDirty);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static TrieNode CreateExtensionTyped(CappedArray<byte> rlp, bool isDirty = false)
            => new TrieNodeExtension(rlp, isDirty);
    }
}
