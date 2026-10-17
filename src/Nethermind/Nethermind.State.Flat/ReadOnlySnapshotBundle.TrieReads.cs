// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

public sealed partial class ReadOnlySnapshotBundle
{
    private static readonly StringLabel _readStateRlpBatchLabel = new("state_rlp_batch");
    private static readonly StringLabel _readStorageRlpBatchLabel = new("storage_rlp_batch");

    internal void LoadStateRlps(ReadOnlySpan<(TreePath Path, Hash256 Hash)> requests, Span<byte[]?> values)
    {
        GuardDispose();
        ArgumentOutOfRangeException.ThrowIfNotEqual(values.Length, requests.Length, nameof(values));
        if (requests.Length < BatchTriePersistenceReaderExtensions.MinBatchSize)
        {
            for (int i = 0; i < requests.Length; i++)
                values[i] = TryLoadStateRlp(in requests[i].Path, requests[i].Hash, ReadFlags.None);
            return;
        }
        using ArrayPoolListRef<int> misses = new(0);
        for (int i = 0; i < requests.Length; i++)
        {
            if (_persistedSnapshotCount > 0 && persistedSnapshots.TryLoadStateRlp(in requests[i].Path, out byte[]? rlp)) values[i] = rlp;
            else misses.Add(i);
        }
        if (misses.Count < BatchTriePersistenceReaderExtensions.MinBatchSize)
        {
            for (int i = 0; i < misses.Count; i++)
            {
                int index = misses[i];
                Nethermind.Trie.Pruning.Metrics.IncrementLoadedFromDbNodesCount();
                long start = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
                values[index] = persistenceReader.TryLoadStateRlp(in requests[index].Path, ReadFlags.None);
                if (recordDetailedMetrics) Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - start, _readStateRlpLabel);
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
        long batchStart = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        persistenceReader.LoadStateRlps(paths, results.AsSpan());
        if (recordDetailedMetrics)
        {
            Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - batchStart, _readStateRlpBatchLabel);
            Metrics.ReadOnlySnapshotBundleReadBatchSize.Observe(misses.Count, _readStateRlpBatchLabel);
        }
        for (int i = 0; i < misses.Count; i++) values[misses[i]] = results[i];
    }

    internal void LoadStorageRlps(Hash256 address, ReadOnlySpan<(TreePath Path, Hash256 Hash)> requests, Span<byte[]?> values)
    {
        GuardDispose();
        ArgumentOutOfRangeException.ThrowIfNotEqual(values.Length, requests.Length, nameof(values));
        if (requests.Length < BatchTriePersistenceReaderExtensions.MinBatchSize)
        {
            for (int i = 0; i < requests.Length; i++)
                values[i] = TryLoadStorageRlp(address, in requests[i].Path, requests[i].Hash, ReadFlags.None);
            return;
        }
        using ArrayPoolListRef<int> misses = new(0);
        for (int i = 0; i < requests.Length; i++)
        {
            if (_persistedSnapshotCount > 0 && persistedSnapshots.TryLoadStorageRlp(address, in requests[i].Path, out byte[]? rlp)) values[i] = rlp;
            else misses.Add(i);
        }
        if (misses.Count < BatchTriePersistenceReaderExtensions.MinBatchSize)
        {
            for (int i = 0; i < misses.Count; i++)
            {
                int index = misses[i];
                Nethermind.Trie.Pruning.Metrics.IncrementLoadedFromDbNodesCount();
                long start = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
                values[index] = persistenceReader.TryLoadStorageRlp(address, in requests[index].Path, ReadFlags.None);
                if (recordDetailedMetrics) Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - start, _readStorageRlpLabel);
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
        long batchStart = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        persistenceReader.LoadStorageRlps(address, paths, results.AsSpan());
        if (recordDetailedMetrics)
        {
            Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - batchStart, _readStorageRlpBatchLabel);
            Metrics.ReadOnlySnapshotBundleReadBatchSize.Observe(misses.Count, _readStorageRlpBatchLabel);
        }
        for (int i = 0; i < misses.Count; i++) values[misses[i]] = results[i];
    }
}
