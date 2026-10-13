// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.State.Flat.Rust.Interop;
using Nethermind.Trie;

namespace Nethermind.State.Flat.Rust.Fork;

/// <summary>A trie node handed to the native manager, which the <c>IsPersisted</c> replay marks once it is persisted.</summary>
/// <param name="Address">The account path of a storage trie node; null for a state trie node.</param>
internal readonly record struct ShellNode(Hash256? Address, TreePath Path, TrieNode Node);

/// <summary>
/// Replays on the C# <see cref="TrieNode"/>s what <c>PersistenceManager.PersistSnapshot</c> does to the nodes it
/// writes: <see cref="TrieNode.IsPersisted"/> and <see cref="TrieNode.PrunePersistedRecursively"/>, so persisted nodes
/// release their children as in the C# backend.
/// </summary>
/// <remarks>
/// <para>
/// The nodes of each added snapshot are kept by state, as the C# repository keeps the snapshot, in the stripes the
/// bundle captured them in, each in the order it set them. A persist of the native manager reports the nodes it wrote
/// for a range of the chain; the newest kept node of each, walking the range back from its end and every stripe back
/// from its end, is the one the C# compacted snapshot would hold and is marked when its hash matches. Every kept
/// snapshot at or below the persisted block is then dropped, as the C# repository drops it. Dropping one prunes the
/// persisted children of every node it kept, as returning a C# snapshot's content prunes those of its nodes.
/// </para>
/// <para>
/// The lock only guards the bookkeeping: a snapshot taken out of it belongs to whoever took it, so marking and
/// releasing nodes never holds up the block processing thread recording the next snapshot.
/// </para>
/// </remarks>
internal sealed class PersistedNodeReplay : IDisposable
{
    private readonly Lock _lock = new();
    private readonly Dictionary<StateId, KeptSnapshot> _snapshots = [];
    private readonly PriorityQueue<StateId, ulong> _byBlock = new();
    // The nodes of the persist being applied, by key, kept at its capacity; only Apply uses it, one call at a time.
    private readonly Dictionary<(bool, ValueHash256, TreePath), ValueHash256> _pending = [];

    /// <summary>The snapshots kept.</summary>
    public int Count
    {
        get
        {
            lock (_lock) return _snapshots.Count;
        }
    }

    /// <summary>
    /// Keeps the trie nodes of the snapshot from <paramref name="from"/> to <paramref name="to"/> about to be added, in
    /// place of any kept for <paramref name="to"/> until <see cref="Settle"/> tells whether the add replaced them.
    /// </summary>
    /// <param name="nodes">The nodes, null for none, which the replay prunes and disposes once it drops them.</param>
    public Addition Record(in StateId from, in StateId to, CapturedNodes? nodes)
    {
        KeptSnapshot kept = new(from, nodes);
        lock (_lock)
        {
            _snapshots.Remove(to, out KeptSnapshot? displaced);
            _snapshots[to] = kept;
            _byBlock.Enqueue(to, to.BlockNumber);
            return new Addition(to, kept, displaced);
        }
    }

    /// <summary>
    /// Settles a <see cref="Record"/> once the native manager answered: an added snapshot keeps its nodes, those it
    /// displaced being of a snapshot the native manager no longer held; a refused one gives the place back to them.
    /// </summary>
    public void Settle(in Addition addition, bool added)
    {
        KeptSnapshot? dropped;
        lock (_lock)
        {
            addition.Kept.IsSettled = true;
            if (!added && _snapshots.TryGetValue(addition.To, out KeptSnapshot? current) && current == addition.Kept)
            {
                if (addition.Displaced is { } displaced) _snapshots[addition.To] = displaced;
                else _snapshots.Remove(addition.To);
                dropped = addition.Kept;
            }
            else
            {
                // Added, or a persist already took the kept nodes and the place with them.
                dropped = addition.Displaced;
            }
        }

        dropped?.Dispose();
    }

    /// <summary>Marks the nodes a persist from <paramref name="from"/> to <paramref name="to"/> wrote, then drops what it covered.</summary>
    /// <remarks>The caller runs one call at a time.</remarks>
    public void Apply(in StateId from, in StateId to, ReadOnlySpan<FfiPersistedNode> persisted)
    {
        using ArrayPoolList<KeptSnapshot> covered = new(0);
        using ArrayPoolList<KeptSnapshot> dropped = new(0);
        lock (_lock)
        {
            StateId state = to;
            while (state != from && _snapshots.Remove(state, out KeptSnapshot? kept))
            {
                covered.Add(kept);
                state = kept.From;
            }

            while (_byBlock.TryPeek(out StateId stale, out ulong blockNumber) && blockNumber <= to.BlockNumber)
            {
                _byBlock.Dequeue();
                if (_snapshots.Remove(stale, out KeptSnapshot? kept)) dropped.Add(kept);
            }
        }

        _pending.Clear();
        foreach (ref readonly FfiPersistedNode node in persisted)
        {
            _pending[node.Key] = node.NodeHash;
        }

        foreach (KeptSnapshot snapshot in covered.AsSpan())
        {
            if (_pending.Count == 0) break;
            if (snapshot.Nodes is not { } nodes) continue;

            foreach (ArrayPoolList<ShellNode>? stripe in nodes.Stripes)
            {
                if (stripe is null) continue;

                // Newest first: a node set again later replaced the earlier one in the C# content.
                ReadOnlySpan<ShellNode> stripeNodes = stripe.AsSpan();
                for (int i = stripeNodes.Length - 1; i >= 0; i--)
                {
                    ShellNode kept = stripeNodes[i];
                    if (!_pending.Remove((kept.Address is not null, kept.Address?.ValueHash256 ?? default, kept.Path), out ValueHash256 hash)) continue;
                    if ((kept.Node.Keccak?.ValueHash256 ?? default) != hash) continue;

                    kept.Node.IsPersisted = true;
                    kept.Node.PrunePersistedRecursively(1);
                }
            }
        }

        _pending.Clear();

        foreach (KeptSnapshot snapshot in covered.AsSpan()) snapshot.Dispose();
        foreach (KeptSnapshot snapshot in dropped.AsSpan()) snapshot.Dispose();
    }

    /// <summary>
    /// Drops the nodes of the snapshots the native manager dropped without persisting them, as
    /// <c>DropStateNotReachableFrom</c> does, so they are released when the C# repository would release them.
    /// </summary>
    /// <param name="isHeld">Whether the native manager holds the snapshot ending at a state.</param>
    /// <param name="persistedState">The native persisted state, read after every <paramref name="isHeld"/>: a snapshot at
    /// or below it is for the persist to release.</param>
    public void DropUnheld(Func<StateId, bool> isHeld, Func<StateId> persistedState)
    {
        List<(StateId State, KeptSnapshot Kept)> settled;
        lock (_lock) settled = [.. _snapshots.Where(static entry => entry.Value.IsSettled).Select(static entry => (entry.Key, entry.Value))];

        List<(StateId State, KeptSnapshot Kept)> unheld = [.. settled.Where(entry => !isHeld(entry.State))];
        if (unheld.Count == 0) return;

        StateId persisted = persistedState();
        List<KeptSnapshot> dropped = [];
        lock (_lock)
        {
            foreach ((StateId state, KeptSnapshot kept) in unheld)
            {
                if (persisted != StateId.PreGenesis && state.BlockNumber <= persisted.BlockNumber) continue;
                if (!_snapshots.TryGetValue(state, out KeptSnapshot? current) || current != kept) continue;

                _snapshots.Remove(state);
                dropped.Add(kept);
            }
        }

        foreach (KeptSnapshot kept in dropped) kept.Dispose();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (KeptSnapshot kept in _snapshots.Values) kept.Dispose();
            _snapshots.Clear();
            _byBlock.Clear();
        }
    }

    /// <summary>A <see cref="Record"/> waiting for <see cref="Settle"/>.</summary>
    /// <param name="Displaced">What was kept for <paramref name="To"/> before.</param>
    internal readonly record struct Addition(StateId To, KeptSnapshot Kept, KeptSnapshot? Displaced);

    /// <summary>The trie nodes of one added snapshot, and the state it was added on.</summary>
    internal sealed class KeptSnapshot(StateId from, CapturedNodes? nodes) : IDisposable
    {
        public StateId From => from;

        /// <summary>The nodes; null for none.</summary>
        public CapturedNodes? Nodes => nodes;

        /// <summary>Whether the native manager answered its add; only then can it be checked against the native manager.</summary>
        public bool IsSettled { get; set; }

        public void Dispose() => nodes?.PruneAndDispose();
    }
}
