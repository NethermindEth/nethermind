// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.Trie.Native;

namespace Nethermind.State.Flat.ScopeProvider;

/// <summary>
/// The <see cref="INativeNodeStore"/> of one trie in a <see cref="SnapshotBundle"/>: the state trie when
/// <paramref name="addressHash"/> is null, otherwise that account's storage trie. Its nodes go through
/// <paramref name="log"/>.
/// </summary>
/// <remarks>
/// Written nodes stay pending in <see cref="NativePendingNodes"/>, where the native bulk set reads them back, until
/// <see cref="Publish"/> moves them into the bundle, so like the managed tries nothing reaches the bundle before the scope
/// commits. The native bulk set writes from several threads at once, which the bundle's node dictionaries do not allow.
/// </remarks>
internal sealed class NativeTrieNodeStore(SnapshotBundle bundle, Hash256? addressHash, NativeTrieNodeLog log) : INativeNodeStore
{
    private NativePendingNodes? _pending;

    /// <summary>Whether nodes were written since the last <see cref="Publish"/> or <see cref="Clear"/>.</summary>
    public bool HasPending => _pending is { Count: > 0 };

    /// <summary>Applies <paramref name="entries"/> to the trie rooted at <paramref name="root"/> and returns the new root.</summary>
    public Hash256 BulkSet(Hash256 root, ReadOnlySpan<PatriciaTree.BulkSetEntry> entries) =>
        NativePatriciaBulkSet.BulkSet(this, _pending ??= NativePendingNodes.Rent(entries.Length), log, bundle.WarmedNodes, addressHash, root.ValueHash256, entries);

    /// <summary>Applies speculative updates without changing this store's pending output.</summary>
    public void Update(NativeBulkSetState prepared, ReadOnlySpan<PatriciaTree.BulkSetEntry> entries)
    {
        TransientResource transient = bundle.TryLeaseTransientResource() ?? throw new ObjectDisposedException(nameof(SnapshotBundle));
        try
        {
            NativePatriciaBulkSet.Update(this, prepared, log, transient.WarmedNodes, entries);
        }
        finally
        {
            transient.ReleaseLease();
        }
    }

    /// <summary>Applies final corrections and hashes the prepared trie into this store's pending output.</summary>
    public Hash256 Finish(NativeBulkSetState prepared, ReadOnlySpan<PatriciaTree.BulkSetEntry> entries)
    {
        if (!bundle.TryLeaseReadOnlyBundle()) throw new ObjectDisposedException(nameof(SnapshotBundle));
        TransientResource? transient = null;
        try
        {
            transient = bundle.TryLeaseTransientResource() ?? throw new ObjectDisposedException(nameof(SnapshotBundle));
            return NativePatriciaBulkSet.Finish(this, prepared, _pending ??= NativePendingNodes.Rent(entries.Length), log, transient.WarmedNodes, entries);
        }
        finally
        {
            transient?.ReleaseLease();
            bundle.ReleaseReadOnlyBundleLease();
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Skips the bundle's changes and its in-memory snapshots: each pins, through <see cref="Snapshot.LogPin"/> or the
    /// scope's pin of the block in progress, the log generation its first node was written to, so the native bulk set
    /// reads all of their nodes from the log, and only fetches persisted ones. Those the trie warmer loaded it reads from
    /// <see cref="SnapshotBundle.WarmedNodes"/> instead.
    /// </remarks>
    public bool Fetch(in TreePath path, in ValueHash256 hash, NativeNodeReceiver receiver) =>
        addressHash is null
            ? bundle.TryLoadStateRlp(path, ReadFlags.None, static (rlp, receiver) => receiver.Receive(rlp), receiver)
            : bundle.TryLoadStorageRlp(addressHash, path, ReadFlags.None, static (rlp, receiver) => receiver.Receive(rlp), receiver);

    /// <summary>Moves the pending nodes into the bundle as their pointers in the log, which holds them.</summary>
    public void Publish()
    {
        if (_pending is null) return;

        long start = Stopwatch.GetTimestamp();
        if (_pending.Count > 0)
        {
            if (addressHash is null) bundle.PublishLoggedStateNodes(_pending);
            else bundle.PublishLoggedStorageNodes(bundle.GetStorageNodeDestination(addressHash), _pending);
        }

        Clear();
        Metrics.AddNativeTrieNodePublishTime(Stopwatch.GetElapsedTime(start).Ticks);
    }

    /// <summary>Drops the pending nodes, as when the storage is cleared.</summary>
    public void Clear()
    {
        _pending?.Dispose();
        _pending = null;
    }
}
