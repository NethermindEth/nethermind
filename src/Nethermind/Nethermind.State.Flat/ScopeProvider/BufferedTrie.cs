// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.Trie.NodeBuffer;

namespace Nethermind.State.Flat.ScopeProvider;

/// <summary>
/// One trie of a <see cref="SnapshotBundle"/>, written by <see cref="BufferBulkSet"/>: the state trie when
/// <paramref name="addressHash"/> is null, otherwise that account's storage trie.
/// </summary>
/// <remarks>
/// Written nodes go to <paramref name="buffer"/> at once, but reach the bundle only on <see cref="Publish"/>, so like the
/// managed tries nothing is visible to the bundle's readers before the scope commits. Until then the bulk sets read them
/// back from the buffer by pointer, which the scope's retain keeps readable.
/// </remarks>
/// <param name="root">The trie's root, as <see cref="BufferBulkSet.ResolveRoot"/> returns it.</param>
internal sealed class BufferedTrie(SnapshotBundle bundle, NodeRecordBuffer buffer, Hash256? addressHash, TrieRoot root) : INodeRecordSource
{
    private Hash256 _rootHash = root.ToHash256();
    private bool _rootDirty;

    public TrieRoot Root { get; private set; } = root;

    public Hash256 RootHash => _rootHash;

    /// <summary>Whether the root changed since the last <see cref="Publish"/>, including a change to empty.</summary>
    public bool HasUnpublishedNodes => _rootDirty;

    /// <inheritdoc cref="BufferBulkSet.BulkSet"/>
    public void BulkSet(in ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries, bool canParallelize) =>
        SetRoot(BufferBulkSet.BulkSet(buffer, this, addressHash, Root, entries, canParallelize));

    /// <summary>Adopts the root of a completed private update.</summary>
    internal void Adopt(in TrieRoot root) => SetRoot(root);

    /// <summary>Makes the final root readable from the bundle.</summary>
    public void Publish()
    {
        // The endpoint state root is needed even for a no-op block following a changed snapshot.
        if (addressHash is not null && !_rootDirty) return;

        long start = Stopwatch.GetTimestamp();
        if (addressHash is null) bundle.PublishBufferedStateRoot(Root);
        else bundle.PublishBufferedStorageRoot(addressHash, Root);

        _rootDirty = false;
        Metrics.TrieNodePublishTime.Observe(Stopwatch.GetTimestamp() - start);
    }

    /// <summary>Empties the trie and drops the unpublished nodes, as when the storage is cleared.</summary>
    public void Clear()
    {
        SetRoot(TrieRoot.Empty);
        _rootDirty = true;
    }

    /// <inheritdoc cref="BufferBulkSet.TryGet"/>
    public byte[]? TryGet(in ValueHash256 key) => BufferBulkSet.TryGet(buffer, this, addressHash, Root, key);

    public bool TryLoad(ulong pointer, in TreePath path, in ValueHash256 hash, out NodeRecord record) =>
        bundle.TryLoadTrieNodeRecord(addressHash, pointer, path, hash, out record);

    public bool TryLoad(in TreePath path, in ValueHash256 hash, out NodeRecord record) =>
        bundle.TryLoadTrieNodeRecord(addressHash, path, hash, out record);

    private void SetRoot(in TrieRoot updated)
    {
        bool hashChanged = updated.Hash != Root.Hash;
        _rootDirty |= hashChanged || updated.Pointer != Root.Pointer;
        if (hashChanged) _rootHash = updated.ToHash256();
        Root = updated;
    }
}
