// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

internal static class PbtTrieWarmer
{
    /// <summary>Reads the groups on a complete key's path without changing the tree.</summary>
    /// <remarks>The store must represent one immutable state for the entire traversal.</remarks>
    internal static void WarmUpPath<TKey>(IPbtStore store, in ValueHash256 root, in TKey key) where TKey : struct, IPbtKey<TKey>
    {
        ArgumentNullException.ThrowIfNull(store);
        if (key.Length == 0) throw new ArgumentException("A complete key is required.", nameof(key));

        PbtStorageNodePath path = new([], 0);
        ValueHash256 groupHash = root;
        Span<byte> groupPathBuffer = stackalloc byte[PbtStorageFullKey.MaxLength];
        while (true)
        {
            PbtNodeGroupLocation<PbtStorageNodePath> location = PbtFourLevelGroupGeometry.Locate(path);
            PbtStorageNodePath groupKey = location.GroupKey;
            PbtTraversalPath groupPath = PbtTraversalPath.FromPath(groupPathBuffer, groupKey);
            using RefCountingMemory? payload = store.GetNodeGroup(groupPath, groupHash);
            if (payload is null) return;

            PbtNodeGroupReader group = new(groupPath, payload.GetSpan());
            do
            {
                if (!group.TryGetNode(location.Position, out ReadOnlySpan<byte> encoding)) return;
                PbtNodeReader node = new(encoding);
                if (node.IsLeaf) return;

                int branchDepth = path.BitDepth + node.Prefix.BitCount;
                if (branchDepth >= key.BitLength) return;
                if (TrieUpdater<TKey, PbtStorageNodePath>.MatchingPrefixBits(node.Prefix, key, path.BitDepth) != node.Prefix.BitCount) return;
                int direction = key.GetBit(branchDepth);
                PbtStorageNodePath childPath = path.Append(node.Prefix, direction);
                location = PbtFourLevelGroupGeometry.Locate(childPath);
                if (!location.GroupKey.Equals(groupKey))
                {
                    groupHash = HashAtBoundary(node, location.GroupKey.BitDepth - path.BitDepth);
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
