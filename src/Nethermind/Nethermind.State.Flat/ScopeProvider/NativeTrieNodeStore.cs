// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.State.Flat.NodeBuffer;
using Nethermind.Trie.Native;

namespace Nethermind.State.Flat.ScopeProvider;

/// <summary>
/// The native node source of one trie in a bundle, publishing only its final root at commit.
/// </summary>
/// <remarks>
/// Pending output is private to this trie until publication. Nodes already reside in the Rust-owned buffer, protected
/// by the scope's pin; the snapshot receives that pin and the final root, not an index of each written node.
/// </remarks>
internal sealed class NativeTrieNodeStore(SnapshotBundle bundle, Hash256? addressHash, INodeRecordBuffer buffer) : INativeNodeStore, IDisposable
{
    private NativePendingNodes? _pending;
    private bool _rootDirty;
    private INativeNodeStore? _warmUpSource;

    internal INativeNodeStore WarmUpSource =>
        Volatile.Read(ref _warmUpSource)
        ?? Interlocked.CompareExchange(ref _warmUpSource, bundle.CreateNativeWarmUpSource(addressHash), null)
        ?? _warmUpSource!;

    /// <summary>Whether the trie has an unpublished root, including a change to empty.</summary>
    public bool HasPending => _rootDirty;

    /// <summary>Applies updates and returns the new root.</summary>
    public Hash256 BulkSet(Hash256 root, ReadOnlySpan<PatriciaTree.BulkSetEntry> entries)
    {
        Hash256 updated = NativePatriciaBulkSet.BulkSet(this, _pending ??= NativePendingNodes.Rent(entries.Length), buffer.Native,
            bundle.NativeWarmedNodes, addressHash, root.ValueHash256, entries);
        _rootDirty |= updated != root || _pending.Count > 0;
        return updated;
    }

    /// <summary>Applies speculative updates without publishing or hashing their result.</summary>
    public void Update(NativeBulkSetState prepared, ReadOnlySpan<PatriciaTree.BulkSetEntry> entries)
    {
        if (!bundle.TryLeaseReadOnlyBundle()) throw new ObjectDisposedException(nameof(SnapshotBundle));
        TransientResource? transient = null;
        try
        {
            transient = bundle.TryLeaseTransientResource() ?? throw new ObjectDisposedException(nameof(SnapshotBundle));
            NativePatriciaBulkSet.Update(this, prepared, buffer.Native, transient.NativeWarmedNodes, entries);
        }
        finally
        {
            transient?.ReleaseLease();
            bundle.ReleaseReadOnlyBundleLease();
        }
    }

    /// <summary>Applies final corrections and hashes a prepared trie, including one needing no corrections.</summary>
    public Hash256 Finish(NativeBulkSetState prepared, ReadOnlySpan<PatriciaTree.BulkSetEntry> entries)
    {
        if (!bundle.TryLeaseReadOnlyBundle()) throw new ObjectDisposedException(nameof(SnapshotBundle));
        TransientResource? transient = null;
        try
        {
            transient = bundle.TryLeaseTransientResource() ?? throw new ObjectDisposedException(nameof(SnapshotBundle));
            Hash256 updated = NativePatriciaBulkSet.Finish(this, prepared, _pending ??= NativePendingNodes.Rent(entries.Length),
                buffer.Native, transient.NativeWarmedNodes, entries);
            _rootDirty |= _pending.Count > 0 || updated == Keccak.EmptyTreeHash;
            return updated;
        }
        finally
        {
            transient?.ReleaseLease();
            bundle.ReleaseReadOnlyBundleLease();
        }
    }

    /// <inheritdoc/>
    /// <remarks>Retained in-memory nodes are reached through the native buffer; the source only fetches persisted nodes.</remarks>
    public bool Fetch(in TreePath path, in ValueHash256 hash, NativeNodeReceiver receiver) =>
        bundle.TryLoadPersistedRlp(addressHash, path, ReadFlags.None, static (rlp, receiver) => receiver.Receive(rlp), receiver);

    /// <summary>Publishes the endpoint state root or a changed storage root.</summary>
    public void Publish(Hash256 root)
    {
        if (addressHash is not null && !_rootDirty) return;

        long start = Stopwatch.GetTimestamp();
        ulong pointer = buffer.FindRoot(addressHash, root.ValueHash256);
        if (addressHash is null) bundle.PublishNativeStateRoot(pointer);
        else bundle.PublishNativeStorageRoot(addressHash, pointer, root.ValueHash256);
        Dispose();
        _rootDirty = false;
        Metrics.AddNativeTrieNodePublishTime(Stopwatch.GetElapsedTime(start).Ticks);
    }

    /// <summary>Drops private output and records a change to empty.</summary>
    public void Clear()
    {
        Dispose();
        _rootDirty = true;
    }

    /// <summary>Returns unpublished output to its pool without publishing a root.</summary>
    public void Dispose()
    {
        _pending?.Dispose();
        _pending = null;
    }
}
