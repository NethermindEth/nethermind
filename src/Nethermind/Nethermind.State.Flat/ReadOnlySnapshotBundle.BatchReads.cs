// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Collections;
using Nethermind.Int256;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.State.Flat;

public sealed partial class ReadOnlySnapshotBundle
{

    private static readonly StringLabel _readStorageBatchLabel = new("storage_batch");

    [SkipLocalsInit]
    internal void GetSlots(ReadOnlySpan<(Address Address, int SelfDestructIdx, UInt256 Slot)> requests, Span<UInt256?> values)
    {
        GuardDispose();
        ArgumentOutOfRangeException.ThrowIfNotEqual(values.Length, requests.Length, nameof(values));
        const int batchSize = 64;
        Span<int> indexes = stackalloc int[batchSize];
        Span<UInt256?> results = stackalloc UInt256?[batchSize];
        for (int start = 0; start < requests.Length; start += batchSize)
        {
            int count = Math.Min(batchSize, requests.Length - start);
            ReadOnlySpan<(Address Address, int SelfDestructIdx, UInt256 Slot)> chunk = requests.Slice(start, count);
            Span<UInt256?> output = values.Slice(start, count);
            int misses = 0;
            for (int i = 0; i < chunk.Length; i++)
            {
                ref readonly (Address Address, int SelfDestructIdx, UInt256 Slot) request = ref chunk[i];
                HashedKey<(Address, UInt256)> key = new((request.Address, request.Slot));
                if (TryGetSlotFromSnapshots(in key, request.SelfDestructIdx, out UInt256? value,
                        recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0))
                    output[i] = value;
                else
                    indexes[misses++] = i;
            }
            if (misses < BatchPersistenceReaderExtensions.MinBatchSize)
            {
                for (int i = 0; i < misses; i++)
                {
                    ref readonly (Address Address, int SelfDestructIdx, UInt256 Slot) request = ref chunk[indexes[i]];
                    UInt256 value = default;
                    long readStart = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
                    output[indexes[i]] = persistenceReader.TryGetSlot(request.Address, in request.Slot, ref value) ? value : null;
                    if (recordDetailedMetrics)
                        Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - readStart,
                            value.IsZero ? _readStoragePersistenceNullLabel : _readStoragePersistenceLabel);
                }
                continue;
            }

            using ArrayPoolListRef<StorageCell> inputs = new(misses, misses);
            for (int i = 0; i < misses; i++)
            {
                ref readonly (Address Address, int SelfDestructIdx, UInt256 Slot) request = ref chunk[indexes[i]];
                inputs[i] = new StorageCell(request.Address, in request.Slot);
            }
            Span<UInt256?> batchValues = misses == chunk.Length ? output : results[..misses];
            long batchStart = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
            persistenceReader.GetSlots(inputs.AsSpan(), batchValues);
            if (recordDetailedMetrics)
            {
                Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - batchStart, _readStorageBatchLabel);
                Metrics.ReadOnlySnapshotBundleReadBatchSize.Observe(misses, _readStorageBatchLabel);
            }
            if (misses != chunk.Length)
                for (int i = 0; i < misses; i++) output[indexes[i]] = batchValues[i];
        }
    }

    internal void GetSlotsAfterCacheMiss(ReadOnlySpan<(Address Address, int SelfDestructIdx, UInt256 Slot)> requests,
        Span<UInt256?> values)
    {
        GuardDispose();
        ArgumentOutOfRangeException.ThrowIfNotEqual(values.Length, requests.Length, nameof(values));
        if (requests.Length < BatchPersistenceReaderExtensions.MinBatchSize)
        {
            for (int i = 0; i < requests.Length; i++)
            {
                ref readonly (Address Address, int SelfDestructIdx, UInt256 Slot) request = ref requests[i];
                UInt256 value = default;
                long readStart = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
                values[i] = persistenceReader.TryGetSlot(request.Address, in request.Slot, ref value) ? value : null;
                if (recordDetailedMetrics)
                    Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - readStart,
                        value.IsZero ? _readStoragePersistenceNullLabel : _readStoragePersistenceLabel);
            }
            return;
        }

        using ArrayPoolListRef<StorageCell> cells = new(requests.Length, requests.Length);
        for (int i = 0; i < requests.Length; i++) cells[i] = new(requests[i].Address, in requests[i].Slot);
        long start = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        persistenceReader.GetSlotsAfterCacheMiss(cells.AsSpan(), values);
        if (recordDetailedMetrics)
        {
            Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - start, _readStorageBatchLabel);
            Metrics.ReadOnlySnapshotBundleReadBatchSize.Observe(requests.Length, _readStorageBatchLabel);
        }
    }

    internal bool TryGetCachedSlot(in HashedKey<(Address, UInt256)> key, int selfDestructIdx, out UInt256? value)
    {
        GuardDispose();
        if (TryGetSlotFromSnapshots(in key, selfDestructIdx, out value,
                recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0)) return true;
        long start = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        (Address address, UInt256 slot) = key.Key;
        if (persistenceReader is not IBatchPersistenceReader batch || !batch.TryGetCachedSlot(address, in slot, out value)) return false;
        if (recordDetailedMetrics)
            Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - start,
                value.GetValueOrDefault().IsZero ? _readStoragePersistenceNullLabel : _readStoragePersistenceLabel);
        return true;
    }

    private bool TryGetSlotFromSnapshots(in HashedKey<(Address, UInt256)> key, int selfDestructIdx, out UInt256? value, long start = 0)
    {
        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            if (snapshots[i].TryGetStorage(key, out value))
            {
                if (recordDetailedMetrics) Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - start, _readStorageSnapshotLabel);
                return true;
            }
            if (_persistedSnapshotCount + i <= selfDestructIdx)
            {
                value = null;
                return true;
            }
        }
        if (_persistedSnapshotCount > 0)
        {
            (Address address, UInt256 slot) = key.Key;
            if (persistedSnapshots.TryGetSlot(address, in slot, selfDestructIdx, start, out value)) return true;
        }
        value = null;
        return false;
    }
}
