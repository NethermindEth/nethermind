// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

/// <summary>One-shot preparation for a single canonical fold, with non-copying zone partitions.</summary>
internal sealed class PbtWriteBatchSet<TKey> : PbtWriteBatchSet, IDisposable where TKey : struct, IPbtKey<TKey>
{
    private ArrayPoolList<PbtWriteOperation<TKey>>? _operations;
    private ArrayPoolList<int>? _table;
    private PartitionOffsets _partitionOffsets;

    private PbtWriteBatchSet(ArrayPoolList<PbtWriteOperation<TKey>> operations, ArrayPoolList<int> table, PartitionOffsets partitionOffsets)
    {
        _operations = operations;
        _table = table;
        _partitionOffsets = partitionOffsets;
    }

    internal int Count => Operations.Count;
    internal ReadOnlySpan<PbtWriteOperation<TKey>> Entries => Operations.AsSpan();
    internal ReadOnlySpan<int> Precalculated
    {
        get
        {
            EnsureNotConsumed();
            return _table!.AsSpan();
        }
    }

    internal ReadOnlySpan<PbtWriteOperation<TKey>> this[PbtPartition partition]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)partition, (uint)PartitionCount);
            return Operations.AsSpan().Slice(_partitionOffsets[(int)partition], _partitionOffsets[(int)partition + 1] - _partitionOffsets[(int)partition]);
        }
    }

    private ArrayPoolList<PbtWriteOperation<TKey>> Operations => _operations ?? throw new InvalidOperationException("The prepared batch has already been consumed.");

    /// <remarks>The source must already contain unique keys; preparation does not deduplicate or drain it.</remarks>
    internal static PbtWriteBatchSet<TKey> Create(IEnumerable<KeyValuePair<TKey, ValueHash256?>> uniqueOperations) =>
        PrepareOperations(uniqueOperations);

    internal static PbtWriteBatchSet<TKey> Create(PbtWriteBatch<TKey> changes)
    {
        changes.Consume(out ArrayPoolList<PbtWriteOperation<TKey>> operations, out ArrayPoolList<int> table);
        using (table)
        {
            try
            {
                return Prepare(operations);
            }
            catch
            {
                operations.Dispose();
                throw;
            }
        }
    }

    internal void Consume(out ArrayPoolList<PbtWriteOperation<TKey>> operations, out ArrayPoolList<int> table)
    {
        operations = Operations;
        table = _table!;
        _operations = null;
        _table = null;
    }

    private void EnsureNotConsumed() => _ = Operations;

    /// <inheritdoc/>
    public void Dispose()
    {
        _operations?.Dispose();
        _table?.Dispose();
        _operations = null;
        _table = null;
    }

    private static PbtWriteBatchSet<TKey> PrepareOperations(IEnumerable<KeyValuePair<TKey, ValueHash256?>> uniqueOperations)
    {
        ArrayPoolList<PbtWriteOperation<TKey>> operations = new(0);
        try
        {
            foreach ((TKey key, ValueHash256? value) in uniqueOperations)
                operations.Add(new(key, value.GetValueOrDefault()));
            return Prepare(operations);
        }
        catch
        {
            operations.Dispose();
            throw;
        }
    }

    private static PbtWriteBatchSet<TKey> Prepare(ArrayPoolList<PbtWriteOperation<TKey>> operations)
    {
        Span<int> counts = stackalloc int[BucketCount];
        counts.Clear();
        foreach (PbtWriteOperation<TKey> operation in operations)
        {
            if (PartitionOf(operation.Key) < 0) return new(operations, new(0), default);

            counts[BucketOf(operation)]++;
        }

        Span<int> next = stackalloc int[BucketCount];
        int total = 0;
        for (int bucket = 0; bucket < BucketCount; bucket++)
        {
            next[bucket] = total;
            total += counts[bucket];
        }

        // Counting distribution groups shards without claiming complete-key sortedness.
        int bucketEnd = 0;
        for (int bucket = 0; bucket < BucketCount; bucket++)
        {
            bucketEnd += counts[bucket];
            while (next[bucket] < bucketEnd)
            {
                int source = next[bucket];
                int destinationBucket = BucketOf(operations[source]);
                int destination = next[destinationBucket]++;
                (operations[source], operations[destination]) = (operations[destination], operations[source]);
            }
        }

        return CreateGrouped(operations, counts);
    }

    /// <summary>Accepts unique operations already ordered by partition and shard.</summary>
    /// <remarks>Takes ownership of operations, including when preparation fails.</remarks>
    internal static PbtWriteBatchSet<TKey> CreateGrouped(ArrayPoolList<PbtWriteOperation<TKey>> operations, ReadOnlySpan<int> counts)
    {
        ArrayPoolList<int>? table = null;
        try
        {
            ArgumentOutOfRangeException.ThrowIfNotEqual(counts.Length, BucketCount);
            PartitionOffsets partitionOffsets = default;
            int total = 0;
            for (int bucket = 0; bucket < BucketCount; bucket++)
            {
                if (bucket % ShardsPerPartition == 0)
                    partitionOffsets[bucket / ShardsPerPartition] = total;
                total += counts[bucket];
            }
            partitionOffsets[PartitionCount] = total;
            ArgumentOutOfRangeException.ThrowIfNotEqual(operations.Count, total);
            int tableLength = total == 0 ? 0 : LevelLength;
            table = new(tableLength, tableLength);
            if (total != 0)
            {
                int countIndex = 1;
                int accountAndCodeCount = partitionOffsets[(int)PbtPartition.Storage];
                if (accountAndCodeCount != 0)
                {
                    table[0] |= 1 << (Eip8297KeyDerivation.AccountZone >> 4);
                    table[countIndex++] = accountAndCodeCount;
                }
                int storageCount = total - accountAndCodeCount;
                if (storageCount != 0)
                {
                    table[0] |= 1 << (Eip8297KeyDerivation.StorageZone >> 4);
                    table[countIndex] = storageCount;
                }
            }
            return new(operations, table, partitionOffsets);
        }
        catch
        {
            operations.Dispose();
            table?.Dispose();
            throw;
        }
    }

    internal static int PartitionOf(TKey key) => key.Length == 0 ? -1 : key.Bytes[0] switch
    {
        Eip8297KeyDerivation.AccountZone when key.Length >= Eip8297KeyDerivation.AccountKeyLength => (int)PbtPartition.Account,
        Eip8297KeyDerivation.CodeZone when key.Length >= Eip8297KeyDerivation.AccountKeyLength => (int)PbtPartition.Code,
        Eip8297KeyDerivation.StorageZone when key.Length >= Eip8297KeyDerivation.StorageKeyLength => (int)PbtPartition.Storage,
        _ => -1,
    };

    private static int BucketOf(in PbtWriteOperation<TKey> operation) =>
        PartitionOf(operation.Key) * ShardsPerPartition + operation.Key.Bytes[1];
}

internal abstract class PbtWriteBatchSet
{
    protected const int PartitionCount = 3;
    protected const int ShardsPerPartition = 256;
    protected const int BucketCount = PartitionCount * ShardsPerPartition;
    protected const int LevelLength = PbtFourLevelGroupGeometry.BoundarySlots + 1;

    [InlineArray(PartitionCount + 1)]
    protected struct PartitionOffsets
    {
        private int _element;
    }
}
