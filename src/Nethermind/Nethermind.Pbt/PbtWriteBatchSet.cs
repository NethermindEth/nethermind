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
                operations.Add(value is { } hash ? PbtWriteOperation<TKey>.Set(key, hash) : PbtWriteOperation<TKey>.Delete(key));
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
            if (PartitionOf(operation.Key) < 0)
            {
                OrderDeletesFirst(operations.AsSpan());
                return new(operations, new(0), default);
            }

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

    /// <summary>Accepts unique operations already ordered by partition, shard, and delete/set bucket.</summary>
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
                if (bucket % (ShardsPerPartition * BucketsPerShard) == 0)
                    partitionOffsets[bucket / (ShardsPerPartition * BucketsPerShard)] = total;
                total += counts[bucket];
            }
            partitionOffsets[PartitionCount] = total;
            ArgumentOutOfRangeException.ThrowIfNotEqual(operations.Count, total);
            int levelCount = CountLevels(counts, partitionOffsets);
            table = new(levelCount * LevelLength, levelCount * LevelLength);
            if (levelCount != 0)
            {
                int tablePosition = 0;
                WriteLevel(table.AsSpan(), ref tablePosition, counts, 0, BucketCount, 0);
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
        (PartitionOf(operation.Key) * ShardsPerPartition + operation.Key.Bytes[1]) * BucketsPerShard +
        (operation.Kind == PbtWriteOperationKind.Delete ? 0 : 1);

    private static void OrderDeletesFirst(Span<PbtWriteOperation<TKey>> operations)
    {
        int deleteCount = 0;
        for (int index = 0; index < operations.Length; index++)
        {
            if (operations[index].Kind != PbtWriteOperationKind.Delete) continue;
            (operations[deleteCount], operations[index]) = (operations[index], operations[deleteCount]);
            deleteCount++;
        }
    }
}

internal abstract class PbtWriteBatchSet
{
    protected const int PartitionCount = 3;
    protected const int ShardsPerPartition = 256;
    protected const int BucketsPerShard = 2;
    protected const int BucketCount = PartitionCount * ShardsPerPartition * BucketsPerShard;
    protected const int LevelLength = 33;

    [InlineArray(PartitionCount + 1)]
    protected struct PartitionOffsets
    {
        private int _element;
    }

    protected static int CountLevels(ReadOnlySpan<int> counts, ReadOnlySpan<int> partitionOffsets)
    {
        if (partitionOffsets[PartitionCount] == 0) return 0;
        int levelCount = 1;
        if (partitionOffsets[2] > 0) levelCount++;
        if (partitionOffsets[3] > partitionOffsets[2]) levelCount++;
        for (int partition = 0; partition < PartitionCount; partition++)
        {
            if (partitionOffsets[partition + 1] == partitionOffsets[partition]) continue;
            levelCount++;
            for (int highNibble = 0; highNibble < 16; highNibble++)
            {
                int start = (partition * ShardsPerPartition + highNibble * 16) * BucketsPerShard;
                foreach (int count in counts.Slice(start, 16 * BucketsPerShard))
                {
                    if (count == 0) continue;
                    levelCount++;
                    break;
                }
            }
        }
        return levelCount;
    }

    protected static void WriteLevel(Span<int> table, ref int tablePosition, ReadOnlySpan<int> counts, int startBucket, int endBucket, int depth)
    {
        int levelStart = tablePosition;
        tablePosition += LevelLength;
        int compactCount = 0;
        for (int bucket = startBucket; bucket < endBucket;)
        {
            int slot = SlotOf(bucket, depth);
            int slotStart = bucket;
            int count = 0;
            do
            {
                count += counts[bucket++];
            } while (bucket < endBucket && SlotOf(bucket, depth) == slot);
            if (count == 0) continue;

            table[levelStart] |= 1 << slot;
            table[levelStart + 1 + compactCount++] = count;
            if (depth == 12) continue;
            table[levelStart + 17 + slot] = tablePosition - levelStart;
            WriteLevel(table, ref tablePosition, counts, slotStart, bucket, depth + 4);
        }
    }

    private static int SlotOf(int bucket, int depth)
    {
        int partition = bucket / (ShardsPerPartition * BucketsPerShard);
        int shard = bucket / BucketsPerShard % ShardsPerPartition;
        return depth switch
        {
            0 => partition == (int)PbtPartition.Storage ? 15 : 0,
            4 => partition == (int)PbtPartition.Storage ? 15 : partition,
            8 => shard >> 4,
            _ => shard & 15,
        };
    }
}
