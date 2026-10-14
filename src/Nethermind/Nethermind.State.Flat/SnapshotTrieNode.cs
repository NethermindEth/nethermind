// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.Trie.Native;

namespace Nethermind.State.Flat;

/// <summary>
/// A trie node held by a snapshot: a <see cref="TrieNode"/>, or with the native bulk set, the pointer of its record in
/// the <see cref="NativeTrieNodeLog"/>.
/// </summary>
/// <remarks>
/// A logged node is read through the log, which keeps it while the snapshot's <see cref="Snapshot.LogPin"/> or, before
/// the commit, the scope's pin of the block in progress is held.
/// </remarks>
public readonly struct SnapshotTrieNode
{
    public SnapshotTrieNode(TrieNode node) => Node = node;

    public SnapshotTrieNode(ulong logPointer) => LogPointer = logPointer;

    /// <summary>The node, or null when it is logged.</summary>
    public TrieNode? Node { get; }

    /// <summary>Where the node's record is in the log, when <see cref="Node"/> is null.</summary>
    public ulong LogPointer { get; }

    public static implicit operator SnapshotTrieNode(TrieNode node) => new(node);

    /// <inheritdoc cref="TrieNodeExtensions.IsHashOnlyPlaceholder"/>
    public bool IsHashOnlyPlaceholder() => Node?.IsHashOnlyPlaceholder() == true;

    /// <summary>The node's RLP, read in place from <paramref name="log"/> for a logged node.</summary>
    public ReadOnlySpan<byte> GetRlp(NativeTrieNodeLog? log) =>
        Node is null ? log!.GetNodeRlp(LogPointer, out _) : Node.FullRlp.AsSpan();

    /// <summary>The node, created from its record in <paramref name="log"/> for a logged node.</summary>
    public TrieNode ToTrieNode(NativeTrieNodeLog? log)
    {
        if (Node is not null) return Node;

        ReadOnlySpan<byte> rlp = log!.GetNodeRlp(LogPointer, out ValueHash256 hash);
        return new TrieNode(NodeType.Unknown, new Hash256(hash), new CappedArray<byte>(rlp.ToArray()));
    }

    /// <summary>Marks a <see cref="TrieNode"/> persisted and prunes its children, which a logged node has none of.</summary>
    public void MarkPersisted()
    {
        if (Node is null) return;

        Node.IsPersisted = true;
        Node.PrunePersistedRecursively(1);
    }
}
