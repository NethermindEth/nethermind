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
    internal static ValueHash256 GetLeafHash<TKey>(IPbtStore store, in ValueHash256 root, in TKey key) where TKey : struct, IPbtKey<TKey>
    {
        ArgumentNullException.ThrowIfNull(store);
        if (key.Length == 0) throw new ArgumentException("A complete key is required.", nameof(key));

        PbtStorageNodePath path = new([], 0);
        ValueHash256 groupHash = root;
        Span<byte> groupPathBuffer = stackalloc byte[PbtStorageTreeKey.MaxLength];
        while (true)
        {
            PbtNodeGroupLocation<PbtStorageNodePath> location = PbtFourLevelGroupGeometry.Locate(path);
            PbtStorageNodePath groupKey = location.GroupKey;
            PbtTraversalPath groupPath = PbtTraversalPath.FromPath(groupPathBuffer, groupKey);
            using RefCountingMemory? payload = store.GetNodeGroup(groupPath, groupHash);
            if (payload is null) return default;

            PbtNodeGroupReader group = PbtNodeGroupReader.FromValidated(groupPath, payload.GetSpan());
            do
            {
                PbtStorageNodePath childPath;
                if (!group.TryGetNode(location.Position, out ReadOnlySpan<byte> encoding))
                {
                    if (PbtFourLevelGroupGeometry.WidthOf(location.Position) is 1 or PbtFourLevelGroupGeometry.BoundarySlots) return default;
                    childPath = path.AppendBits(key.GetBit(path.BitDepth), 1);
                    location = PbtFourLevelGroupGeometry.Locate(childPath);
                }
                else
                {
                    PbtNodeReader node = PbtNodeReader.FromValidated(encoding);
                    if (node.IsLeaf) return node.Key.SequenceEqual(key.Bytes) ? groupHash : default;

                    int branchDepth = path.BitDepth + node.Prefix.BitCount;
                    if (branchDepth >= key.BitLength) return default;
                    if (TrieUpdater<TKey, PbtStorageNodePath>.MatchingPrefixBits(node.Prefix, key, path.BitDepth) != node.Prefix.BitCount) return default;
                    int direction = key.GetBit(branchDepth);
                    ReadOnlySpan<byte> inlineLeafKey = direction == 0 ? node.LeftKey : node.RightKey;
                    if (!inlineLeafKey.IsEmpty) return inlineLeafKey.SequenceEqual(key.Bytes) ? (direction == 0 ? node.LeftHash : node.RightHash) : default;
                    childPath = path.Append(node.Prefix, direction);
                    location = PbtFourLevelGroupGeometry.Locate(childPath);
                    if (!location.GroupKey.Equals(groupKey))
                    {
                        groupHash = HashAtBoundary(node, location.GroupKey.BitDepth - path.BitDepth);
                    }
                }
                path = childPath;
            } while (location.GroupKey.Equals(groupKey));
        }
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
