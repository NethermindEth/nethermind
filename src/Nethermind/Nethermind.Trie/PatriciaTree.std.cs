// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Trie;

public partial class PatriciaTree
{
    /// <summary>Whether a walk to a value keeps the path of the node it has reached.</summary>
    /// <remarks>Path-keyed node storage finds a node by its path, so the walk has to track it. A property rather
    /// than a const, which would make every guarded statement unreachable code (CS0162) in the guest build.</remarks>
    private static bool TracksPath => true;

    /// <summary>How <see cref="ShouldUpdateChild"/> is inlined: left to the JIT.</summary>
    private const MethodImplOptions ShouldUpdateChildInlining = default;

    /// <summary>Whether a trie write may stop climbing at this level, the rest of the climb changing nothing: never.</summary>
    /// <remarks>A node can be shared with the trie store's caches here, so a dirty parent says nothing about its ancestors.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsUnchangedPendingLevel(TrieNode parent, int childIndex, TrieNode? originalChild, TrieNode? child) => false;
}
