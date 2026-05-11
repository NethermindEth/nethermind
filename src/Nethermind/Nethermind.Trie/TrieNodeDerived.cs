// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie
{
    // B3a introduced sealed runtime types for branch / leaf / extension as empty
    // subclasses of TrieNode. The base class still owns _nodeData and every
    // behavior; these subclasses exist so that B3b can rebind callers to typed
    // instances (returned from a non-mutating decode) and B3c can replace the
    // remaining `_nodeData is X` type-tests with `this is TrieNodeX`. C# does
    // not allow changing an object's runtime type, so the typed identity must
    // be established at construction. Decoders that already know the shape from
    // the RLP header route through the CreateXxxTyped static factories below.
    //
    // B3pre extends the typed factories so every test-construction site that
    // used to call `new TrieNode(NodeType.Branch|Leaf|Extension, ...)` allocates
    // a typed instance. The base TrieNode still owns _nodeData; B4 will move
    // shape state into the derived classes and delete NodeData.cs.

    internal sealed class TrieNodeBranch : TrieNode
    {
        internal TrieNodeBranch() : base(new BranchData()) { }
        internal TrieNodeBranch(BranchData data) : base(data) { }
        internal TrieNodeBranch(in ValueHash256 keccak) : base(new BranchData(), in keccak) { }
        internal TrieNodeBranch(CappedArray<byte> rlp, bool isDirty)
            : base(new BranchData(), rlp, isDirty) { }
        internal TrieNodeBranch(in ValueHash256 keccak, CappedArray<byte> rlp)
            : base(new BranchData(), rlp, in keccak) { }
    }

    internal sealed class TrieNodeLeaf : TrieNode
    {
        internal TrieNodeLeaf() : base(new LeafData()) { }
        internal TrieNodeLeaf(LeafData data) : base(data) { }
        internal TrieNodeLeaf(in ValueHash256 keccak) : base(new LeafData(), in keccak) { }
        internal TrieNodeLeaf(CappedArray<byte> rlp, bool isDirty)
            : base(new LeafData(), rlp, isDirty) { }
        internal TrieNodeLeaf(in ValueHash256 keccak, CappedArray<byte> rlp)
            : base(new LeafData(), rlp, in keccak) { }
    }

    internal sealed class TrieNodeExtension : TrieNode
    {
        internal TrieNodeExtension() : base(new ExtensionData()) { }
        internal TrieNodeExtension(ExtensionData data) : base(data) { }
        internal TrieNodeExtension(in ValueHash256 keccak) : base(new ExtensionData(), in keccak) { }
        internal TrieNodeExtension(CappedArray<byte> rlp, bool isDirty)
            : base(new ExtensionData(), rlp, isDirty) { }
        internal TrieNodeExtension(in ValueHash256 keccak, CappedArray<byte> rlp)
            : base(new ExtensionData(), rlp, in keccak) { }
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
            => new TrieNodeLeaf(new LeafData(hexPrefixedKey, value));

        /// <summary>
        /// Allocate a typed empty leaf node. The key and value are filled in by the
        /// caller (used by mutating builders).
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
            => new TrieNodeExtension(new ExtensionData(hexPrefixedKey));

        /// <summary>
        /// Allocate a typed extension node carrying an already hex-prefixed key
        /// and an in-memory child reference.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static TrieNode CreateExtensionTyped(byte[] hexPrefixedKey, TrieNode child)
            => new TrieNodeExtension(new ExtensionData(hexPrefixedKey, child));

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
