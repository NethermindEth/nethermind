// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

/// <summary>One-shot preparation for a single canonical fold, with non-copying zone partitions.</summary>
internal sealed class PbtWriteBatchSet
{
    private const int PartitionCount = 3;
    private const int ShardsPerPartition = 256;
    private const int BucketsPerShard = 2;
    private const int BucketCount = PartitionCount * ShardsPerPartition * BucketsPerShard;
    private const int LevelLength = 33;

    private PbtWriteOperation[]? _operations;
    private int[] _table;
    private readonly int[] _partitionOffsets;

    private PbtWriteBatchSet(PbtWriteOperation[] operations, int[] table, int[] partitionOffsets)
    {
        _operations = operations;
        _table = table;
        _partitionOffsets = partitionOffsets;
    }

    internal int Count => Operations.Length;
    internal ReadOnlySpan<PbtWriteOperation> Entries => Operations;
    internal ReadOnlySpan<int> Precalculated
    {
        get
        {
            EnsureNotConsumed();
            return _table;
        }
    }

    internal ReadOnlySpan<PbtWriteOperation> this[PbtPartition partition]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)partition, (uint)PartitionCount);
            return Operations.AsSpan(_partitionOffsets[(int)partition], _partitionOffsets[(int)partition + 1] - _partitionOffsets[(int)partition]);
        }
    }

    private PbtWriteOperation[] Operations => _operations ?? throw new InvalidOperationException("The prepared batch has already been consumed.");

    /// <remarks>The source must already contain unique keys; preparation does not deduplicate or drain it.</remarks>
    internal static PbtWriteBatchSet Create(IEnumerable<KeyValuePair<PbtFullKey, ValueHash256?>> uniqueOperations) =>
        Prepare([.. EnumerateOperations(uniqueOperations)]);

    internal static PbtWriteBatchSet Create(PbtWriteBatch changes) => Prepare([.. changes.Operations]);

    internal void Consume(out PbtWriteOperation[] operations, out int[] table)
    {
        operations = Operations;
        table = _table;
        _operations = null;
        _table = [];
    }

    private void EnsureNotConsumed() => _ = Operations;

    private static IEnumerable<PbtWriteOperation> EnumerateOperations(IEnumerable<KeyValuePair<PbtFullKey, ValueHash256?>> uniqueOperations)
    {
        foreach ((PbtFullKey key, ValueHash256? value) in uniqueOperations)
        {
            yield return value is { } hash ? PbtWriteOperation.Set(key, hash) : PbtWriteOperation.Delete(key);
        }
    }

    private static PbtWriteBatchSet Prepare(PbtWriteOperation[] operations)
    {
        Span<int> counts = stackalloc int[BucketCount];
        counts.Clear();
        foreach (PbtWriteOperation operation in operations)
        {
            if (PartitionOf(operation.Key) < 0)
            {
                OrderDeletesFirst(operations);
                return new(operations, [], new int[PartitionCount + 1]);
            }

            counts[BucketOf(operation)]++;
        }

        int[] partitionOffsets = new int[PartitionCount + 1];
        Span<int> next = stackalloc int[BucketCount];
        int total = 0;
        for (int bucket = 0; bucket < BucketCount; bucket++)
        {
            if (bucket % (ShardsPerPartition * BucketsPerShard) == 0)
                partitionOffsets[bucket / (ShardsPerPartition * BucketsPerShard)] = total;
            next[bucket] = total;
            total += counts[bucket];
        }
        partitionOffsets[PartitionCount] = total;

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

        int levelCount = CountLevels(counts, partitionOffsets);
        int[] table = levelCount == 0 ? [] : new int[levelCount * LevelLength];
        if (levelCount != 0)
        {
            int tablePosition = 0;
            WriteLevel(table, ref tablePosition, counts, 0, BucketCount, 0);
        }
        return new(operations, table, partitionOffsets);
    }

    private static int PartitionOf(PbtFullKey key) => key.Bytes[0] switch
    {
        Eip8297KeyDerivation.AccountZone when key.Length >= Eip8297KeyDerivation.AccountKeyLength => (int)PbtPartition.Account,
        Eip8297KeyDerivation.CodeZone when key.Length >= Eip8297KeyDerivation.AccountKeyLength => (int)PbtPartition.Code,
        Eip8297KeyDerivation.StorageZone when key.Length >= Eip8297KeyDerivation.StorageKeyLength => (int)PbtPartition.Storage,
        _ => -1,
    };

    private static int BucketOf(in PbtWriteOperation operation) =>
        (PartitionOf(operation.Key) * ShardsPerPartition + operation.Key.Bytes[1]) * BucketsPerShard +
        (operation.Kind == PbtWriteOperationKind.Delete ? 0 : 1);

    private static void OrderDeletesFirst(Span<PbtWriteOperation> operations)
    {
        int deleteCount = 0;
        for (int index = 0; index < operations.Length; index++)
        {
            if (operations[index].Kind != PbtWriteOperationKind.Delete) continue;
            (operations[deleteCount], operations[index]) = (operations[index], operations[deleteCount]);
            deleteCount++;
        }
    }

    private static int CountLevels(ReadOnlySpan<int> counts, ReadOnlySpan<int> partitionOffsets)
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

    private static void WriteLevel(int[] table, ref int tablePosition, ReadOnlySpan<int> counts, int startBucket, int endBucket, int depth)
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
