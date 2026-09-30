// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

internal static class PbtNodeTraverser
{
    /// <summary>Follows a complete key from the root through the node groups and returns its leaf hash, or default when the key is absent.</summary>
    /// <remarks>
    /// The store must represent one immutable state for the entire traversal. A prefixless interior branch is not
    /// stored (<see cref="PbtNodeGroupCodec.ShouldOmit"/>); its position is descended as an implicit branch whose
    /// children lie in the same group.
    /// </remarks>
    /// <param name="pinnedGroups">
    /// Top groups of the same state shared across traversals, read from without the store and filled from it; null
    /// fetches every group from the store.
    /// </param>
    internal static ValueHash256 GetLeafHash<TKey>(IPbtStore store, in ValueHash256 root, in TKey key, PbtPinnedGroups? pinnedGroups) where TKey : unmanaged, IPbtKey<TKey>
    {
        ArgumentNullException.ThrowIfNull(store);
        if (key.Length == 0) throw new ArgumentException("A complete key is required.", nameof(key));

        PbtStorageNodePath path = new([], 0);
        ValueHash256 groupHash = root;
        Span<byte> groupPathBuffer = stackalloc byte[PbtStorageTreeKey.MaxLength];
        PbtNodeGroupLocation<PbtStorageNodePath> location = PbtFourLevelGroupGeometry.Locate(path);
        // A pinned group is known by its path alone, so the hash keying the store is only computed for the others.
        RefCountingMemory? pinned = pinnedGroups?.Get(location.GroupKey);
        while (true)
        {
            PbtStorageNodePath groupKey = location.GroupKey;
            PbtTraversalPath groupPath = PbtTraversalPath.FromPath(groupPathBuffer, groupKey);
            using RefCountingMemory? lease = pinned is null ? store.GetNodeGroup(groupPath, groupHash) : null;
            RefCountingMemory? payload = pinned ?? lease;
            if (payload is null) return default;
            if (lease is not null) pinnedGroups?.TryPin(groupKey, lease);

            PbtNodeGroupReader group = PbtNodeGroupReader.FromValidated(groupPath, payload.GetSpan());
            do
            {
                PbtStorageNodePath childPath;
                if (!group.TryGetNode(location.Position, out ReadOnlySpan<byte> encoding))
                {
                    if (PbtFourLevelGroupGeometry.WidthOf(location.Position) is 1 or PbtFourLevelGroupGeometry.BoundarySlots) return default;
                    if (!TryDescendToStored(group, key, groupKey, path, out childPath, out location)) return default;
                }
                else
                {
                    PbtNodeReader node = PbtNodeReader.FromValidated(encoding);
                    if (node.IsLeaf) return node.Key.SequenceEqual(key.Bytes) ? groupHash : default;

                    int branchDepth = path.BitDepth + node.Prefix.BitCount;
                    if (branchDepth >= key.BitLength) return default;
                    if (TrieUpdater<TKey, PbtStorageNodePath>.MatchingPrefixBits(node.Prefix, key, path.BitDepth) != node.Prefix.BitCount) return default;
                    int direction = key.GetBit(branchDepth);
                    ReadOnlySpan<byte> inlineLeafKey = direction == 0 ? node.LeftKeyPostfix : node.RightKeyPostfix;
                    if (!inlineLeafKey.IsEmpty)
                        return inlineLeafKey.SequenceEqual(key.Bytes[(groupKey.BitDepth >> 3)..]) ? (direction == 0 ? node.LeftHash : node.RightHash) : default;
                    childPath = path.Append(node.Prefix, direction);
                    location = PbtFourLevelGroupGeometry.Locate(childPath);
                    if (!location.GroupKey.Equals(groupKey))
                    {
                        pinned = pinnedGroups?.Get(location.GroupKey);
                        if (pinned is null) groupHash = HashAtBoundary(node, location.GroupKey.BitDepth - path.BitDepth);
                    }
                }
                path = childPath;
            } while (location.GroupKey.Equals(groupKey));
        }
    }

    /// <summary>Finds the first node <paramref name="group"/> stores on the key's path below the interior <paramref name="path"/> it leaves implicit.</summary>
    /// <remarks>
    /// Only prefixless interior branches are left out, so a dense group stores nothing above its boundary. Its levels are
    /// checked by position alone, and the path is extended once, to the node found.
    /// </remarks>
    /// <returns>False when the group stores no node on the key's path, so the key is absent.</returns>
    private static bool TryDescendToStored<TKey>(PbtNodeGroupReader group, in TKey key, PbtStorageNodePath groupKey, PbtStorageNodePath path,
        out PbtStorageNodePath storedPath, out PbtNodeGroupLocation<PbtStorageNodePath> location)
        where TKey : unmanaged, IPbtKey<TKey>
    {
        int groupDepth = groupKey.BitDepth;
        int implicitDepth = path.BitDepth - groupDepth;
        // Group depths are nibble-aligned, and a key reaching into a group spans all of its levels.
        int slot = (key.Bytes[groupDepth >> 3] >> (4 - (groupDepth & 4))) & 0xF;
        for (int depth = implicitDepth + 1; depth <= PbtFourLevelGroupGeometry.LevelsPerGroup; depth++)
        {
            int position = new NodeGroupPath(slot & ~((PbtFourLevelGroupGeometry.BoundarySlots >> depth) - 1), depth).Position;
            if (!group.TryGetNode(position, out _)) continue;
            int descended = depth - implicitDepth;
            storedPath = path.AppendBits((slot >> (PbtFourLevelGroupGeometry.LevelsPerGroup - depth)) & ((1 << descended) - 1), descended);
            location = new(groupKey, position);
            return true;
        }
        storedPath = default;
        location = default;
        return false;
    }

    private static ValueHash256 HashAtBoundary(PbtNodeReader node, int prefixOffset)
    {
        if (prefixOffset == 0) return PbtNodeCodec.Hash(node);
        int prefixLength = node.Prefix.BitCount - prefixOffset;
        Span<byte> normalized = stackalloc byte[67 + PbtBitPrefix.ByteCount(prefixLength)];
        PbtNodeCodec.CreateBranchEncoding(normalized, prefixLength, node.LeftHash, node.RightHash);
        PbtBitPrefix.CopyBits(node.Prefix.Bytes, prefixOffset, prefixLength, normalized[3..], 0);
        return Blake3Hash.Hash(normalized);
    }
}
