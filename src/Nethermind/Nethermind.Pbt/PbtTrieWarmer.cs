// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Buffers;

namespace Nethermind.Pbt;

internal static class PbtTrieWarmer
{
    /// <summary>Reads the groups on a complete key's path without changing the tree.</summary>
    /// <remarks>The store must represent one immutable state for the entire traversal.</remarks>
    internal static void WarmUpPath(IPbtStore store, in PbtStorageFullKey key)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (key.Length == 0) throw new ArgumentException("A complete key is required.", nameof(key));

        PbtStorageNodePath path = new([], 0);
        while (true)
        {
            PbtNodeGroupLocation<PbtStorageNodePath> location = PbtFourLevelGroupGeometry.Locate(path);
            using RefCountingMemory? payload = store.GetNodeGroup(location.GroupKey);
            if (payload is null) return;

            PbtNodeGroupReader<PbtStorageNodePath> group = new(location.GroupKey, payload.GetSpan());
            do
            {
                if (!group.TryGetNode(location.Position, out ReadOnlySpan<byte> encoding)) return;
                PbtNodeReader node = new(encoding);
                if (node.IsLeaf) return;

                int branchDepth = path.BitDepth + node.Prefix.BitCount;
                if (branchDepth >= key.BitLength) return;
                for (int bit = 0; bit < node.Prefix.BitCount; bit++)
                    if (TrieUpdater.GetBit(key.Bytes, path.BitDepth + bit) != TrieUpdater.GetBit(node.Prefix.Bytes, bit)) return;

                int direction = TrieUpdater.GetBit(key.Bytes, branchDepth);
                path = path.Append(node.Prefix, direction);
                location = PbtFourLevelGroupGeometry.Locate(path);
            } while (location.GroupKey.Equals(group.GroupKey));
        }
    }
}
