// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

internal static class PbtTrieWarmer
{
    /// <summary>Reads the groups on a complete key's path without changing the tree.</summary>
    /// <remarks>The store must represent one immutable state for the entire traversal.</remarks>
    /// <param name="pinnedGroups">Top groups of the same state shared across warm-ups; null fetches every group from the store.</param>
    internal static void WarmUpPath<TKey>(IPbtStore store, in ValueHash256 root, in TKey key, PbtPinnedGroups? pinnedGroups) where TKey : unmanaged, IPbtKey<TKey> =>
        PbtNodeTraverser.GetLeafHash(store, root, key, pinnedGroups);
}
