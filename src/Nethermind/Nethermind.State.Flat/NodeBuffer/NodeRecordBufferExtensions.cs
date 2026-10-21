// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Trie;

namespace Nethermind.State.Flat.NodeBuffer;

/// <summary>Operations on the shared node-record representation, independent of buffer storage policy.</summary>
public static class NodeRecordBufferExtensions
{
    /// <summary>Copies the buffer-resident node at the exact path from the given root, or misses at an unavailable child.</summary>
    /// <remarks>Inline children have no separate record and are not returned.</remarks>
    internal static bool TryGetNode(this INodeRecordBuffer buffer, ulong rootPointer, in TreePath path, [NotNullWhen(true)] out TrieNode? node) =>
        buffer.Native.TryGetNode(rootPointer, path, out node);

    /// <summary>Enumerates reachable buffer records in depth-first path order, stopping at unavailable child pointers.</summary>
    /// <remarks>
    /// The caller holds a pin protecting the records for as long as it reads them.
    /// Inline children are contained in their parent's RLP and are not emitted as separate records.
    /// Pointers below <paramref name="lowerBound"/> and their subtrees are skipped: immutable buffer parents are written after their children.
    /// </remarks>
    public static IEnumerable<(TreePath Path, ulong Pointer)> EnumerateNodes(this INodeRecordBuffer buffer, ulong rootPointer, ulong lowerBound) =>
        buffer.Native.EnumerateNodes(rootPointer, lowerBound);
}
