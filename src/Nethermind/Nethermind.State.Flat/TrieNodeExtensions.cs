// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Trie;

namespace Nethermind.State.Flat;

internal static class TrieNodeExtensions
{
    /// <summary>
    /// Whether the node is a hash-only placeholder: an undecoded reference carrying no RLP, which is what a
    /// warmer miss and an unresolved <c>FindCachedOrUnknown</c> result look like.
    /// </summary>
    /// <remarks>
    /// Such a node holds nothing to write or promote, so persistence, the persisted-snapshot builders and the
    /// shared <see cref="TrieNodeCache"/> all skip it.
    /// </remarks>
    public static bool IsHashOnlyPlaceholder(this TrieNode node) =>
        node.NodeType == NodeType.Unknown && node.FullRlp.Length == 0;
}
