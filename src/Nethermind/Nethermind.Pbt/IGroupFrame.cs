// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

/// <summary>The group a fold composes one frame against: a stored <see cref="GroupFrameReader{TKey, TPath}"/> or an <see cref="AbsentGroupFrame{TKey, TPath}"/>.</summary>
/// <remarks>The fold is generic over the frame, so the absent case is decided once where the frame opens and costs no check per access.</remarks>
internal interface IGroupFrame<TKey, TPath>
    where TKey : unmanaged, IPbtKey<TKey>
    where TPath : struct, IPbtNodePath<TPath>
{
    int BitDepth { get; }

    /// <summary>The stored payload's length, zero for a group that is not stored.</summary>
    int PayloadLength { get; }

    /// <summary>The summed payload lengths of the groups physically stored below boundary slot <paramref name="slot"/>.</summary>
    long DescendantBytes(int slot);

    /// <summary>The boundary slots whose <see cref="DescendantBytes"/> may be nonzero; every other slot is zero.</summary>
    ushort DescendantMask { get; }

    /// <summary>The positions the group stores an encoding at.</summary>
    uint StoredPositions { get; }

    /// <summary>The encoding stored at <paramref name="position"/>, or empty when the group leaves it out.</summary>
    ReadOnlyMemory<byte> GetEncoding(int position);

    /// <summary>Copies the stored encodings from <paramref name="startPosition"/> up to <paramref name="endPosition"/> into <paramref name="writer"/>, returning how many were copied.</summary>
    int CopyRange(PbtNodeGroupWriter<TPath> writer, int startPosition, int endPosition);

    /// <summary>Takes the boundary node stored at <paramref name="position"/>.</summary>
    /// <param name="hash">The node's hash: the one its parent's link holds where a link names it, and otherwise the one its encoding needs.</param>
    TrieUpdater<TKey, TPath>.BoundaryNode TakeBoundaryNode(int position, in ValueHash256 hash);

    /// <summary>Takes the leaf inlined in the branch at <paramref name="position"/>, which stores no node of its own.</summary>
    TrieUpdater<TKey, TPath>.BoundaryNode TakeInlineLeaf(int position, bool right);
}
