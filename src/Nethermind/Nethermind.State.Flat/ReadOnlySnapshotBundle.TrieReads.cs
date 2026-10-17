// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

public sealed partial class ReadOnlySnapshotBundle
{
    internal void LoadStateRlps(ReadOnlySpan<(TreePath Path, Hash256 Hash)> requests, Span<byte[]?> values)
    {
        GuardDispose();
        ArgumentOutOfRangeException.ThrowIfNotEqual(values.Length, requests.Length, nameof(values));
        using ArrayPoolListRef<int> misses = new(0);
        for (int i = 0; i < requests.Length; i++)
        {
            if (recordDetailedMetrics) values[i] = TryLoadStateRlp(in requests[i].Path, requests[i].Hash, ReadFlags.None);
            else if (_persistedSnapshotCount > 0 && persistedSnapshots.TryLoadStateRlp(in requests[i].Path, out byte[]? rlp)) values[i] = rlp;
            else misses.Add(i);
        }
        if (misses.Count < BatchTriePersistenceReaderExtensions.MinBatchSize)
        {
            for (int i = 0; i < misses.Count; i++)
            {
                int index = misses[i];
                Nethermind.Trie.Pruning.Metrics.IncrementLoadedFromDbNodesCount();
                values[index] = persistenceReader.TryLoadStateRlp(in requests[index].Path, ReadFlags.None);
            }
            return;
        }
        using ArrayPoolSpan<TreePath> paths = new(misses.Count);
        using ArrayPoolListRef<byte[]?> results = new(misses.Count, misses.Count);
        for (int i = 0; i < misses.Count; i++)
        {
            paths[i] = requests[misses[i]].Path;
            Nethermind.Trie.Pruning.Metrics.IncrementLoadedFromDbNodesCount();
        }
        persistenceReader.LoadStateRlps(paths, results.AsSpan());
        for (int i = 0; i < misses.Count; i++) values[misses[i]] = results[i];
    }

    internal void LoadStorageRlps(Hash256 address, ReadOnlySpan<(TreePath Path, Hash256 Hash)> requests, Span<byte[]?> values)
    {
        GuardDispose();
        ArgumentOutOfRangeException.ThrowIfNotEqual(values.Length, requests.Length, nameof(values));
        using ArrayPoolListRef<int> misses = new(0);
        for (int i = 0; i < requests.Length; i++)
        {
            if (recordDetailedMetrics) values[i] = TryLoadStorageRlp(address, in requests[i].Path, requests[i].Hash, ReadFlags.None);
            else if (_persistedSnapshotCount > 0 && persistedSnapshots.TryLoadStorageRlp(address, in requests[i].Path, out byte[]? rlp)) values[i] = rlp;
            else misses.Add(i);
        }
        if (misses.Count < BatchTriePersistenceReaderExtensions.MinBatchSize)
        {
            for (int i = 0; i < misses.Count; i++)
            {
                int index = misses[i];
                Nethermind.Trie.Pruning.Metrics.IncrementLoadedFromDbNodesCount();
                values[index] = persistenceReader.TryLoadStorageRlp(address, in requests[index].Path, ReadFlags.None);
            }
            return;
        }
        using ArrayPoolSpan<TreePath> paths = new(misses.Count);
        using ArrayPoolListRef<byte[]?> results = new(misses.Count, misses.Count);
        for (int i = 0; i < misses.Count; i++)
        {
            paths[i] = requests[misses[i]].Path;
            Nethermind.Trie.Pruning.Metrics.IncrementLoadedFromDbNodesCount();
        }
        persistenceReader.LoadStorageRlps(address, paths, results.AsSpan());
        for (int i = 0; i < misses.Count; i++) values[misses[i]] = results[i];
    }
}
