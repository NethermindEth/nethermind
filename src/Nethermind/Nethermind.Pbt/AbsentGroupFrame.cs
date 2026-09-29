// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

/// <summary>A frame for a group that is not stored, which the fold composes from its boundary node alone.</summary>
/// <remarks>
/// A group is absent when its boundary node owns none: an empty subtree, a leaf, a branch over two inlined leaves, or a
/// branch whose prefix spans past the group, and the tree root's group when the tree is empty. A spanning branch keeps
/// the descendants its owner recorded under the boundary slot, all below its own slot here. The same frame stands in
/// for an owner frame that cannot be shared across threads, where only its depth and one slot's size are read.
/// Nothing is stored, so there is no node to take.
/// </remarks>
internal readonly struct AbsentGroupFrame<TKey, TPath> : IGroupFrame<TKey, TPath>
    where TKey : unmanaged, IPbtKey<TKey>
    where TPath : struct, IPbtNodePath<TPath>
{
    private readonly ushort _descendantMask;
    private readonly long _descendantBytes;

    internal AbsentGroupFrame(int bitDepth) => BitDepth = bitDepth;

    /// <param name="slot">The boundary slot everything stored below the group lies under.</param>
    /// <param name="descendantBytes">The summed payload lengths of the groups stored below <paramref name="slot"/>.</param>
    internal AbsentGroupFrame(int bitDepth, int slot, long descendantBytes) : this(bitDepth)
    {
        _descendantMask = descendantBytes == 0 ? (ushort)0 : (ushort)(1 << slot);
        _descendantBytes = descendantBytes;
    }

    public int BitDepth { get; }

    public int PayloadLength => 0;

    public long DescendantBytes(int slot) => (_descendantMask & (1 << slot)) == 0 ? 0 : _descendantBytes;

    public ushort DescendantMask => _descendantMask;

    public uint StoredPositions => 0;

    public ReadOnlyMemory<byte> GetEncoding(int position) => throw NoStoredNode();

    public int CopyRange(PbtNodeGroupWriter<TPath> writer, int startPosition, int endPosition) => 0;

    public TrieUpdater<TKey, TPath>.BoundaryNode TakeBoundaryNode(int position, in ValueHash256 hash) => throw NoStoredNode();

    public TrieUpdater<TKey, TPath>.BoundaryNode TakeInlineLeaf(int position, bool right) => throw NoStoredNode();

    private static InvalidOperationException NoStoredNode() => new("An absent PBT node group stores no node.");
}
