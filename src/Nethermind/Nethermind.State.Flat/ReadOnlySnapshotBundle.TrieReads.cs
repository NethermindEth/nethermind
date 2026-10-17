// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

public sealed partial class ReadOnlySnapshotBundle
{
    private static readonly StringLabel _readStateRlpBatchLabel = new("state_rlp_batch");
    private static readonly StringLabel _readStorageRlpBatchLabel = new("storage_rlp_batch");

    internal void LoadStateRlps(ReadOnlySpan<(TreePath Path, Hash256 Hash)> requests, Span<byte[]?> values) =>
        LoadRlps(requests, values, new StateRlpReader());

    internal void LoadStorageRlps(Hash256 address, ReadOnlySpan<(TreePath Path, Hash256 Hash)> requests, Span<byte[]?> values) =>
        LoadRlps(requests, values, new StorageRlpReader(address));

    private static readonly StringLabel _readMixedRlpBatchLabel = new("trie_rlp_batch");

    internal void LoadRlps(ReadOnlySpan<(Hash256? Address, TreePath Path)> requests, Span<byte[]?> values)
    {
        GuardDispose();
        ArgumentOutOfRangeException.ThrowIfNotEqual(values.Length, requests.Length, nameof(values));
        Span<int> indexes = stackalloc int[BatchTriePersistenceReaderExtensions.BatchSize];
        for (int start = 0; start < requests.Length; start += BatchTriePersistenceReaderExtensions.BatchSize)
        {
            int count = Math.Min(BatchTriePersistenceReaderExtensions.BatchSize, requests.Length - start);
            ReadOnlySpan<(Hash256? Address, TreePath Path)> chunk = requests.Slice(start, count);
            Span<byte[]?> output = values.Slice(start, count);
            int misses = 0;
            if (_persistedSnapshotCount == 0) misses = count;
            else
            {
                for (int i = 0; i < count; i++)
                {
                    bool found = chunk[i].Address is { } address
                        ? persistedSnapshots.TryLoadStorageRlp(address, in chunk[i].Path, out output[i])
                        : persistedSnapshots.TryLoadStateRlp(in chunk[i].Path, out output[i]);
                    if (!found) indexes[misses++] = i;
                }
            }
            for (int i = 0; i < misses; i++) Nethermind.Trie.Pruning.Metrics.IncrementLoadedFromDbNodesCount();
            if (misses < BatchTriePersistenceReaderExtensions.MinBatchSize)
            {
                for (int i = 0; i < misses; i++)
                {
                    int index = misses == count ? i : indexes[i];
                    long readStart = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
                    output[index] = chunk[index].Address is { } address
                        ? persistenceReader.TryLoadStorageRlp(address, in chunk[index].Path, ReadFlags.None)
                        : persistenceReader.TryLoadStateRlp(in chunk[index].Path, ReadFlags.None);
                    if (recordDetailedMetrics)
                        Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - readStart,
                            chunk[index].Address is null ? _readStateRlpLabel : _readStorageRlpLabel);
                }
                continue;
            }
            if (misses == count)
            {
                ReadRlps(chunk, output);
                continue;
            }
            using ArrayPoolListRef<(Hash256? Address, TreePath Path)> inputs = new(misses, misses);
            using ArrayPoolListRef<byte[]?> results = new(misses, misses);
            for (int i = 0; i < misses; i++) inputs[i] = chunk[indexes[i]];
            ReadRlps(inputs.AsSpan(), results.AsSpan());
            for (int i = 0; i < misses; i++) output[indexes[i]] = results[i];
        }
    }

    private void ReadRlps(ReadOnlySpan<(Hash256? Address, TreePath Path)> requests, Span<byte[]?> values)
    {
        long start = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        persistenceReader.LoadRlps(requests, values);
        if (recordDetailedMetrics)
        {
            Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - start, _readMixedRlpBatchLabel);
            Metrics.ReadOnlySnapshotBundleReadBatchSize.Observe(requests.Length, _readMixedRlpBatchLabel);
        }
    }

    [SkipLocalsInit]
    private void LoadRlps<TReader>(ReadOnlySpan<(TreePath Path, Hash256 Hash)> requests, Span<byte[]?> values, TReader reader)
        where TReader : struct, IRlpReader
    {
        GuardDispose();
        ArgumentOutOfRangeException.ThrowIfNotEqual(values.Length, requests.Length, nameof(values));
        if (requests.Length < BatchTriePersistenceReaderExtensions.MinBatchSize)
        {
            for (int i = 0; i < requests.Length; i++) values[i] = reader.Read(this, in requests[i]);
            return;
        }

        Span<TreePath> paths = stackalloc TreePath[BatchTriePersistenceReaderExtensions.BatchSize];
        Span<int> indexes = stackalloc int[BatchTriePersistenceReaderExtensions.BatchSize];
        for (int start = 0; start < requests.Length; start += BatchTriePersistenceReaderExtensions.BatchSize)
        {
            int count = Math.Min(BatchTriePersistenceReaderExtensions.BatchSize, requests.Length - start);
            ReadOnlySpan<(TreePath Path, Hash256 Hash)> chunk = requests.Slice(start, count);
            Span<byte[]?> output = values.Slice(start, count);
            int misses = 0;
            if (_persistedSnapshotCount == 0)
            {
                for (int i = 0; i < chunk.Length; i++) paths[i] = chunk[i].Path;
                misses = chunk.Length;
            }
            else
            {
                for (int i = 0; i < chunk.Length; i++)
                {
                    if (reader.TryReadSnapshot(persistedSnapshots, in chunk[i].Path, out byte[]? rlp)) output[i] = rlp;
                    else
                    {
                        paths[misses] = chunk[i].Path;
                        indexes[misses++] = i;
                    }
                }
            }
            for (int i = 0; i < misses; i++) Nethermind.Trie.Pruning.Metrics.IncrementLoadedFromDbNodesCount();
            if (misses < BatchTriePersistenceReaderExtensions.MinBatchSize)
            {
                for (int i = 0; i < misses; i++)
                {
                    long readStart = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
                    output[misses == chunk.Length ? i : indexes[i]] = reader.ReadPersistence(persistenceReader, in paths[i]);
                    if (recordDetailedMetrics) Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - readStart, reader.ScalarLabel);
                }
                continue;
            }
            if (misses == chunk.Length)
            {
                ReadRlps(paths[..misses], output, reader);
                continue;
            }

            using ArrayPoolListRef<byte[]?> results = new(misses, misses);
            ReadRlps(paths[..misses], results.AsSpan(), reader);
            for (int i = 0; i < misses; i++) output[indexes[i]] = results[i];
        }
    }

    private void ReadRlps<TReader>(ReadOnlySpan<TreePath> paths, Span<byte[]?> values, TReader reader)
        where TReader : struct, IRlpReader
    {
        long start = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        reader.ReadBatch(persistenceReader, paths, values);
        if (recordDetailedMetrics)
        {
            Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - start, reader.BatchLabel);
            Metrics.ReadOnlySnapshotBundleReadBatchSize.Observe(paths.Length, reader.BatchLabel);
        }
    }

    private interface IRlpReader
    {
        StringLabel ScalarLabel { get; }
        StringLabel BatchLabel { get; }
        byte[]? Read(ReadOnlySnapshotBundle bundle, in (TreePath Path, Hash256 Hash) request);
        bool TryReadSnapshot(PersistedSnapshotStack persistedSnapshots, in TreePath path, out byte[]? value);
        byte[]? ReadPersistence(IPersistence.IPersistenceReader reader, in TreePath path);
        void ReadBatch(IPersistence.IPersistenceReader reader, ReadOnlySpan<TreePath> paths, Span<byte[]?> values);
    }

    private readonly struct StateRlpReader : IRlpReader
    {
        public StringLabel ScalarLabel => _readStateRlpLabel;
        public StringLabel BatchLabel => _readStateRlpBatchLabel;
        public byte[]? Read(ReadOnlySnapshotBundle bundle, in (TreePath Path, Hash256 Hash) request) =>
            bundle.TryLoadStateRlp(in request.Path, request.Hash, ReadFlags.None);
        public bool TryReadSnapshot(PersistedSnapshotStack persistedSnapshots, in TreePath path, out byte[]? value) =>
            persistedSnapshots.TryLoadStateRlp(in path, out value);
        public byte[]? ReadPersistence(IPersistence.IPersistenceReader reader, in TreePath path) =>
            reader.TryLoadStateRlp(in path, ReadFlags.None);
        public void ReadBatch(IPersistence.IPersistenceReader reader, ReadOnlySpan<TreePath> paths, Span<byte[]?> values) =>
            reader.LoadStateRlps(paths, values);
    }

    private readonly struct StorageRlpReader(Hash256 address) : IRlpReader
    {
        public StringLabel ScalarLabel => _readStorageRlpLabel;
        public StringLabel BatchLabel => _readStorageRlpBatchLabel;
        public byte[]? Read(ReadOnlySnapshotBundle bundle, in (TreePath Path, Hash256 Hash) request) =>
            bundle.TryLoadStorageRlp(address, in request.Path, request.Hash, ReadFlags.None);
        public bool TryReadSnapshot(PersistedSnapshotStack persistedSnapshots, in TreePath path, out byte[]? value) =>
            persistedSnapshots.TryLoadStorageRlp(address, in path, out value);
        public byte[]? ReadPersistence(IPersistence.IPersistenceReader reader, in TreePath path) =>
            reader.TryLoadStorageRlp(address, in path, ReadFlags.None);
        public void ReadBatch(IPersistence.IPersistenceReader reader, ReadOnlySpan<TreePath> paths, Span<byte[]?> values) =>
            reader.LoadStorageRlps(address, paths, values);
    }
}
