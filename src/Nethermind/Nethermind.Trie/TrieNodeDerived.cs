// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie
{
    // B3a: introduce sealed runtime types for branch / leaf / extension as empty
    // subclasses of TrieNode. The base class still owns _nodeData and every
    // behavior; these subclasses exist so that B3b can rebind callers to typed
    // instances (returned from a non-mutating decode) and B3c can replace the
    // remaining `_nodeData is X` type-tests with `this is TrieNodeX`. C# does
    // not allow changing an object's runtime type, so the typed identity must
    // be established at construction. Decoders that already know the shape from
    // the RLP header route through the CreateXxxTyped static factories below.
    //
    // No abstract on the base yet. Existing `new TrieNode(NodeType.Unknown, ...)`
    // placeholder construction sites continue to produce base TrieNode instances
    // until B3b eliminates in-place decode.

    internal sealed class TrieNodeBranch : TrieNode
    {
        internal TrieNodeBranch() : base(new BranchData()) { }
        internal TrieNodeBranch(BranchData data) : base(data) { }
    }

    internal sealed class TrieNodeLeaf : TrieNode
    {
        internal TrieNodeLeaf() : base(new LeafData()) { }
        internal TrieNodeLeaf(LeafData data) : base(data) { }
    }

    internal sealed class TrieNodeExtension : TrieNode
    {
        internal TrieNodeExtension() : base(new ExtensionData()) { }
        internal TrieNodeExtension(ExtensionData data) : base(data) { }
    }

    public partial class TrieNode
    {
        /// <summary>
        /// Allocate a typed dirty branch node. Use this in decode paths and dirty
        /// constructors so the runtime type matches <see cref="NodeType.Branch"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static TrieNode CreateBranchTyped() => new TrieNodeBranch();

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
        internal static TrieNode CreateLeafTyped() => new TrieNodeLeaf();

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
    }
}
