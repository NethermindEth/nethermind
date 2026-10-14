// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.Trie.NodeLog;

namespace Nethermind.State.Flat.ScopeProvider;

/// <summary>
/// One trie of a <see cref="SnapshotBundle"/>, written by <see cref="LogBulkSet"/>: the state trie when
/// <paramref name="addressHash"/> is null, otherwise that account's storage trie.
/// </summary>
/// <remarks>
/// Written nodes go to <paramref name="log"/> at once, but reach the bundle only on <see cref="Publish"/>, so like the
/// managed tries nothing is visible to the bundle's readers before the scope commits. Until then the bulk sets read them
/// back from the log by pointer, which the scope's retain keeps readable.
/// </remarks>
/// <param name="root">The trie's root, as <see cref="LogBulkSet.ResolveRoot"/> returns it.</param>
internal sealed class LoggedTrie(SnapshotBundle bundle, TrieNodeLog log, Hash256? addressHash, TrieRoot root) : INodeRecordSource, IDisposable
{
    private readonly WrittenNodes _written = new();
    private Hash256 _rootHash = root.ToHash256();

    public TrieRoot Root { get; private set; } = root;

    public Hash256 RootHash => _rootHash;

    /// <summary>Whether nodes were written since the last <see cref="Publish"/> or <see cref="Clear"/>.</summary>
    public bool HasUnpublishedNodes => _written.Count > 0;

    /// <inheritdoc cref="LogBulkSet.BulkSet"/>
    public void BulkSet(in ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries, bool canParallelize) =>
        SetRoot(LogBulkSet.BulkSet(log, this, addressHash, Root, entries, _written, canParallelize));

    /// <summary>Moves the written nodes into the bundle as their pointers in the log.</summary>
    public void Publish()
    {
        if (_written.Count == 0) return;

        long start = Stopwatch.GetTimestamp();
        if (addressHash is null) bundle.PublishLoggedStateNodes(_written);
        else bundle.PublishLoggedStorageNodes(bundle.GetStorageNodeDestination(addressHash), _written);

        _written.Clear();
        Metrics.AddTrieNodePublishTime(Stopwatch.GetElapsedTime(start).Ticks);
    }

    /// <summary>Empties the trie and drops the unpublished nodes, as when the storage is cleared.</summary>
    public void Clear()
    {
        SetRoot(TrieRoot.Empty);
        _written.Clear();
    }

    /// <inheritdoc cref="LogBulkSet.TryGet"/>
    public byte[]? TryGet(in ValueHash256 key) => LogBulkSet.TryGet(log, this, addressHash, Root, key);

    public bool TryLoad(in TreePath path, in ValueHash256 hash, out NodeRecordRef record) =>
        bundle.TryLoadTrieNodeRecord(addressHash, path, hash, out record);

    public void Dispose() => _written.Dispose();

    private void SetRoot(in TrieRoot updated)
    {
        if (updated.Hash != Root.Hash) _rootHash = updated.ToHash256();
        Root = updated;
    }
}
