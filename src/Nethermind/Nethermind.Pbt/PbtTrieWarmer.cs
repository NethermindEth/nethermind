// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

internal static class PbtTrieWarmer
{
    /// <summary>The stored size below the next node group under which warming stops, as a group that small is one read for the fold.</summary>
    internal const long DefaultMinSubtreeBytes = 32 * 1024;

    /// <summary>Reads the groups on a complete key's path without changing the tree, and returns whether it stopped before the leaf.</summary>
    /// <remarks>The store must represent one immutable state for the entire traversal.</remarks>
    /// <param name="minSubtreeBytes">The stored size below the next node group under which warming stops instead of fetching it; zero or less warms the whole path.</param>
    internal static bool WarmUpPath<TKey>(IPbtStore store, in ValueHash256 root, in TKey key, long minSubtreeBytes) where TKey : struct, IPbtKey<TKey>
    {
        PbtNodeTraverser.GetLeafHash(store, root, key, minSubtreeBytes, out bool stoppedAtSmallSubtree);
        return stoppedAtSmallSubtree;
    }
}
