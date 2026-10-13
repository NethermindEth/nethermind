// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Trie;

namespace Nethermind.State.Flat.Rust.Fork;

/// <summary>
/// The trie nodes a <see cref="SnapshotBundle"/> set since its last commit, kept for the <c>IsPersisted</c> replay: the
/// storage nodes in <see cref="StorageStripes"/> stripes by the first byte of the account path, the state nodes in one more.
/// </summary>
/// <remarks>
/// Each stripe is a list under a lock of its own, created with its first node. The storage trie of an account is written
/// by one thread at a time, so the nodes of an account are in the order they were set. No native call runs under a
/// stripe lock.
/// </remarks>
internal sealed class NodeCaptures
{
    public const int StorageStripes = 16;
    private const int StateStripe = StorageStripes;
    private const int InitialStripeCapacity = 16;

    private readonly Lock[] _locks = [.. Enumerable.Range(0, StorageStripes + 1).Select(static _ => new Lock())];
    private readonly ArrayPoolList<ShellNode>?[] _stripes = new ArrayPoolList<ShellNode>?[StorageStripes + 1];

    public void AddState(in TreePath path, TrieNode node) => Add(StateStripe, new ShellNode(null, path, node));

    public void AddStorage(Hash256 addressHash, in TreePath path, TrieNode node) =>
        Add(StripeOf(addressHash), new ShellNode(addressHash, path, node));

    /// <summary>Drops the storage nodes of the account hashing to <paramref name="addressHash"/>, without pruning them.</summary>
    public void RemoveAddress(Hash256 addressHash)
    {
        int stripe = StripeOf(addressHash);
        lock (_locks[stripe])
        {
            if (_stripes[stripe] is not { } nodes) return;

            Span<ShellNode> span = nodes.AsSpan();
            int kept = 0;
            foreach (ShellNode node in span)
            {
                if (node.Address != addressHash) span[kept++] = node;
            }

            nodes.Truncate(kept);
        }
    }

    /// <summary>Moves the nodes out, as the stripes hold them; null when there are none.</summary>
    public CapturedNodes? Take()
    {
        ArrayPoolList<ShellNode>?[]? taken = null;
        for (int stripe = 0; stripe < _stripes.Length; stripe++)
        {
            ArrayPoolList<ShellNode>? nodes;
            lock (_locks[stripe])
            {
                nodes = _stripes[stripe];
                _stripes[stripe] = null;
            }

            if (nodes is not null) (taken ??= new ArrayPoolList<ShellNode>?[_stripes.Length])[stripe] = nodes;
        }

        return taken is null ? null : new CapturedNodes(taken);
    }

    public void PruneAndDispose() => Take()?.PruneAndDispose();

    private static int StripeOf(Hash256 addressHash) => addressHash.Bytes[0] & (StorageStripes - 1);

    private void Add(int stripe, in ShellNode node)
    {
        lock (_locks[stripe])
        {
            (_stripes[stripe] ??= new ArrayPoolList<ShellNode>(InitialStripeCapacity)).Add(node);
        }
    }
}

/// <summary>
/// The trie nodes of one commit of a <see cref="SnapshotBundle"/>, in the stripes of <see cref="NodeCaptures"/>: the
/// storage nodes by the first byte of the account path, then the state nodes; each stripe in the order they were set.
/// </summary>
internal sealed class CapturedNodes(ArrayPoolList<ShellNode>?[] stripes) : IDisposable
{
    /// <summary>The stripes; a stripe no node was set in is null.</summary>
    public ReadOnlySpan<ArrayPoolList<ShellNode>?> Stripes => stripes;

    /// <summary>Prunes the persisted children of every node, as releasing the C# content does, then disposes.</summary>
    public void PruneAndDispose()
    {
        foreach (ArrayPoolList<ShellNode>? nodes in stripes)
        {
            if (nodes is null) continue;
            foreach (ShellNode node in nodes.AsSpan()) node.Node.PrunePersistedRecursively(1);
        }

        Dispose();
    }

    public void Dispose()
    {
        for (int stripe = 0; stripe < stripes.Length; stripe++)
        {
            stripes[stripe]?.Dispose();
            stripes[stripe] = null;
        }
    }
}
