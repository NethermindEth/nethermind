// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Trie.Sparse;

/// <summary>
/// Compact arena node for the sparse trie. Extensions are folded into branches via <see cref="ShortKey"/>.
/// <remarks>
/// Branch children are stored externally in a dense <see cref="SparseChildEntry"/> array per-subtrie,
/// indexed via <see cref="ChildrenStart"/> and <see cref="StateMask"/>.
/// Blinded nodes store their hash/RLP in <see cref="CachedRlp"/>.
/// Leaf values are stored in a separate byte[][] array, indexed by <see cref="ValueIndex"/>.
/// </remarks>
/// </summary>
[StructLayout(LayoutKind.Auto)]
public struct SparseTrieNode
{
    public SparseNodeKind Kind;
    public SparseNodeState State;

    /// <summary>Branch: which nibbles 0-15 have children. Unused for other kinds.</summary>
    public TrieMask StateMask;

    /// <summary>Branch: which children are blinded (hash-only stubs). Subset of <see cref="StateMask"/>.</summary>
    public TrieMask BlindedMask;

    /// <summary>
    /// Branch: extension prefix (empty if pure branch). A branch with non-empty ShortKey
    /// represents a merged extension+branch node.
    /// Leaf: the remaining key nibbles after the path prefix.
    /// </summary>
    public byte[]? ShortKey;

    /// <summary>Leaf: index into the subtrie's Values array. -1 if not a leaf.</summary>
    public int ValueIndex;

    /// <summary>Branch: start index into the subtrie's Children array for this node's dense children.</summary>
    public int ChildrenStart;

    /// <summary>Cached child-ref encoding after hashing. For nodes with RLP >= 32 bytes, this is the
    /// 32-byte keccak hash. For smaller nodes, this is the raw inline RLP. Used by parent encoding.</summary>
    public RlpNode CachedRlp;

    /// <summary>Full canonical RLP of this node (before hashing to child-ref form). Used for persistence.
    /// For nodes with RLP >= 32 bytes: differs from CachedRlp (which is the hash). For smaller nodes: same as CachedRlp.
    /// Set during EncodeLeaf/EncodeBranch. Null until hashed.</summary>
    public byte[]? FullRlp;

    /// <summary>For branch-with-ShortKey (folded extension+branch): the inner branch RLP before extension wrapping.
    /// Both the extension wrapper (FullRlp) and inner branch (InnerBranchRlp) may need separate DB entries.
    /// Null for pure branches without extension prefix.</summary>
    public byte[]? InnerBranchRlp;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool IsEmpty() => Kind == SparseNodeKind.Empty;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool IsBranch() => Kind == SparseNodeKind.Branch;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool IsLeaf() => Kind == SparseNodeKind.Leaf;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool IsBlinded() => Kind == SparseNodeKind.Blinded;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool IsDirty() => State == SparseNodeState.Dirty;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool IsCached() => State == SparseNodeState.Cached;

    /// <summary>True if this branch has an extension prefix (merged extension+branch).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool HasShortKey() => ShortKey is not null && ShortKey.Length > 0;

    /// <summary>Number of children in this branch (popcount of StateMask).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly int ChildCount() => StateMask.CountBits();

    /// <summary>
    /// Returns the dense index in the Children array for the given nibble.
    /// Only valid if <see cref="StateMask"/> has the nibble bit set.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly int DenseChildIndex(int nibble) => ChildrenStart + StateMask.DenseIndex(nibble);

    public void MarkDirty()
    {
        State = SparseNodeState.Dirty;
        CachedRlp = default;
        FullRlp = null;
        InnerBranchRlp = null;
    }

    public static SparseTrieNode CreateEmpty() => new()
    {
        Kind = SparseNodeKind.Empty,
        State = SparseNodeState.Dirty,
        ValueIndex = -1,
        ChildrenStart = -1,
    };

    public static SparseTrieNode CreateLeaf(byte[] key, int valueIndex) => new()
    {
        Kind = SparseNodeKind.Leaf,
        State = SparseNodeState.Dirty,
        ShortKey = key,
        ValueIndex = valueIndex,
        ChildrenStart = -1,
    };

    public static SparseTrieNode CreateBranch(TrieMask stateMask, int childrenStart) => new()
    {
        Kind = SparseNodeKind.Branch,
        State = SparseNodeState.Dirty,
        StateMask = stateMask,
        ValueIndex = -1,
        ChildrenStart = childrenStart,
    };

    public static SparseTrieNode CreateBranchWithExtension(byte[] shortKey, TrieMask stateMask, int childrenStart) => new()
    {
        Kind = SparseNodeKind.Branch,
        State = SparseNodeState.Dirty,
        ShortKey = shortKey,
        StateMask = stateMask,
        ValueIndex = -1,
        ChildrenStart = childrenStart,
    };

    public static SparseTrieNode CreateBlinded(RlpNode cachedRlp) => new()
    {
        Kind = SparseNodeKind.Blinded,
        State = SparseNodeState.Cached,
        CachedRlp = cachedRlp,
        ValueIndex = -1,
        ChildrenStart = -1,
    };

    public override readonly string ToString() => Kind switch
    {
        SparseNodeKind.Empty => "Empty",
        SparseNodeKind.Leaf => $"Leaf(key={ShortKey?.Length ?? 0}nib, val={ValueIndex}, {State})",
        SparseNodeKind.Branch => HasShortKey()
            ? $"Ext+Branch(ext={ShortKey!.Length}nib, children={ChildCount()}, {State})"
            : $"Branch(children={ChildCount()}, {State})",
        SparseNodeKind.Blinded => $"Blinded({CachedRlp.Length}B)",
        _ => $"Unknown({Kind})"
    };
}
