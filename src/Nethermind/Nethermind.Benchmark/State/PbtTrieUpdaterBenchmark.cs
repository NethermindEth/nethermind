// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using Nethermind.Pbt;

namespace Nethermind.Benchmarks.State;

/// <summary>Measures the sorted-range <c>UpdateRootSorted</c> on one thread, in parallel and through the partitioned driver.</summary>
/// <remarks>
/// Every invocation applies one of <see cref="BatchVariants"/> prepared batches to the same base tree: the store keeps the
/// base groups apart and discards what the previous invocation wrote, so no invocation sees another's changes. Keys are
/// account-zone keys, the shape the partitioned driver expects.
/// </remarks>
[MemoryDiagnoser]
public class PbtTrieUpdaterBenchmark
{
    public enum Variant
    {
        /// <summary>The sorted-range updater, given the sorted batch.</summary>
        Sorted,
        /// <summary>The sorted-range updater, sorting a shuffled copy of the batch first.</summary>
        SortedIncludingSort,
        /// <summary>The sorted-range updater folding each frame's slots across threads, given the sorted batch.</summary>
        SortedParallel,
        /// <summary>The sorted-range updater through the partitioned driver, sorting the shards and folding slots across threads, given the same shard-grouped batch.</summary>
        SortedPartitioned,
    }

    private const int BatchVariants = 64;
    private const int BuildChunk = 1 << 16;

    private PbtOverlayStore _store = null!;
    private readonly ConcurrencyController _foldQuota = new(Environment.ProcessorCount);
    private ValueHash256 _root;
    private Batch[] _batches = null!;
    private int _next;

    [Params(100_000, 1_000_000)]
    public int TreeSize { get; set; }

    [Params(1, 16, 256, 4096)]
    public int BatchSize { get; set; }

    [Params(Variant.Sorted, Variant.SortedIncludingSort, Variant.SortedParallel, Variant.SortedPartitioned)]
    public Variant Updater { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        Random random = new(8297);
        _store = new PbtOverlayStore();
        PbtPath[] keys = new PbtPath[TreeSize];
        for (int index = 0; index < keys.Length; index++) keys[index] = RandomKey(random);

        PbtWriteOperation<PbtPath>[] chunk = new PbtWriteOperation<PbtPath>[BuildChunk];
        for (int start = 0; start < keys.Length; start += BuildChunk)
        {
            int count = Math.Min(BuildChunk, keys.Length - start);
            for (int index = 0; index < count; index++) chunk[index] = new(keys[start + index], RandomValue(random));
            Array.Sort(chunk, 0, count, OperationComparer.Instance);
            _root = TrieUpdater<PbtPath, PbtNodePath>.UpdateRootSorted(_store, _root, chunk.AsSpan(0, count), PbtPrefixlessBranchOmission.Interior);
        }
        _store.CommitOverlay();

        _batches = new Batch[BatchVariants];
        for (int variant = 0; variant < BatchVariants; variant++) _batches[variant] = CreateBatch(random, keys);
    }

    [GlobalCleanup]
    public void GlobalCleanup() => _store.Dispose();

    [Benchmark]
    public ValueHash256 UpdateRoot()
    {
        _store.ResetOverlay();
        Batch batch = _batches[_next++ % BatchVariants];
        switch (Updater)
        {
            case Variant.Sorted:
                {
                    using ArrayPoolList<PbtWriteOperation<PbtPath>> operations = new(batch.Sorted);
                    return TrieUpdater<PbtPath, PbtNodePath>.UpdateRootSorted(_store, _root, operations.AsSpan(), PbtPrefixlessBranchOmission.Interior);
                }
            case Variant.SortedPartitioned:
                {
                    using PbtPartitionBatches batches = new()
                    {
                        Account = new PbtWriteBatch<PbtPath>(new ArrayPoolList<PbtWriteOperation<PbtPath>>(batch.Sharded), new ArrayPoolList<int>(batch.ZoneTable),
                            ZoneShardNibbleIndex),
                    };
                    return TrieUpdater.UpdateRoot(_store, _root, batches, _foldQuota, FoldFanOut.Default, PbtPrefixlessBranchOmission.Interior, null);
                }
            case Variant.SortedParallel:
                {
                    using ArrayPoolList<PbtWriteOperation<PbtPath>> operations = new(batch.Sorted);
                    return TrieUpdater<PbtPath, PbtNodePath>.UpdateRootSorted(_store, _root, operations.UnsafeGetInternalArray().AsMemory(0, operations.Count),
                        PbtPrefixlessBranchOmission.Interior, _foldQuota, FoldFanOut.Default);
                }
            default:
                {
                    using ArrayPoolList<PbtWriteOperation<PbtPath>> operations = new(batch.Shuffled);
                    operations.AsSpan().Sort(OperationComparer.Instance);
                    return TrieUpdater<PbtPath, PbtNodePath>.UpdateRootSorted(_store, _root, operations.AsSpan(), PbtPrefixlessBranchOmission.Interior);
                }
        }
    }

    /// <summary>A batch of about 70% updates, 20% inserts and 10% deletes over distinct keys.</summary>
    private Batch CreateBatch(Random random, PbtPath[] keys)
    {
        Dictionary<PbtPath, ValueHash256> writes = new(BatchSize);
        while (writes.Count < BatchSize)
        {
            int choice = random.Next(10);
            PbtPath key = choice < 8 ? keys[random.Next(keys.Length)] : RandomKey(random);
            writes[key] = choice == 0 ? default : RandomValue(random);
        }

        PbtWriteOperation<PbtPath>[] shuffled = new PbtWriteOperation<PbtPath>[writes.Count];
        int offset = 0;
        foreach ((PbtPath key, ValueHash256 value) in writes) shuffled[offset++] = new(key, value);
        random.Shuffle(shuffled);
        PbtWriteOperation<PbtPath>[] sorted = (PbtWriteOperation<PbtPath>[])shuffled.Clone();
        Array.Sort(sorted, OperationComparer.Instance);

        return new Batch(sorted, shuffled, GroupByShard(shuffled, ZoneShardNibbleIndex), ShardTable(sorted, ZoneShardNibbleIndex));
    }

    /// <summary>The partitioned driver shards a zone's batch by the nibble just below the zone byte.</summary>
    internal const int ZoneShardNibbleIndex = 2;

    /// <summary>The operations grouped by the shard nibble at <paramref name="nibbleIndex"/> in shard order, each shard keeping their order, as PbtWriteBatchBuilder lays them out.</summary>
    internal static PbtWriteOperation<TKey>[] GroupByShard<TKey>(PbtWriteOperation<TKey>[] operations, int nibbleIndex) where TKey : struct, IPbtKey<TKey>
    {
        int[] starts = new int[17];
        foreach (PbtWriteOperation<TKey> operation in operations) starts[ShardOf(operation.Key, nibbleIndex) + 1]++;
        for (int shard = 0; shard < 16; shard++) starts[shard + 1] += starts[shard];
        PbtWriteOperation<TKey>[] grouped = new PbtWriteOperation<TKey>[operations.Length];
        foreach (PbtWriteOperation<TKey> operation in operations) grouped[starts[ShardOf(operation.Key, nibbleIndex)]++] = operation;
        return grouped;
    }

    private static int ShardOf<TKey>(TKey key, int nibbleIndex) where TKey : struct, IPbtKey<TKey> =>
        (key.Bytes[nibbleIndex >> 1] >> ((nibbleIndex & 1) == 0 ? 4 : 0)) & 15;

    /// <summary>The shard table PbtWriteBatchBuilder builds at <paramref name="nibbleIndex"/>: the used-shard mask, then each used shard's count.</summary>
    /// <remarks>Sorted operations already lie grouped by shard, in shard order, as the batch expects.</remarks>
    internal static int[] ShardTable<TKey>(ReadOnlySpan<PbtWriteOperation<TKey>> sorted, int nibbleIndex) where TKey : struct, IPbtKey<TKey>
    {
        int[] counts = new int[16];
        foreach (PbtWriteOperation<TKey> operation in sorted) counts[ShardOf(operation.Key, nibbleIndex)]++;
        List<int> table = [0];
        for (int shard = 0; shard < counts.Length; shard++)
        {
            if (counts[shard] == 0) continue;
            table[0] |= 1 << shard;
            table.Add(counts[shard]);
        }
        return [.. table];
    }

    private static PbtPath RandomKey(Random random)
    {
        Span<byte> bytes = stackalloc byte[PbtPath.KeyLength];
        random.NextBytes(bytes);
        bytes[0] = Eip8297KeyDerivation.AccountZone;
        return new PbtPath(bytes);
    }

    private static ValueHash256 RandomValue(Random random)
    {
        Span<byte> bytes = stackalloc byte[32];
        random.NextBytes(bytes);
        bytes[0] |= 1;
        return new ValueHash256(bytes);
    }

    private sealed record Batch(PbtWriteOperation<PbtPath>[] Sorted, PbtWriteOperation<PbtPath>[] Shuffled, PbtWriteOperation<PbtPath>[] Sharded, int[] ZoneTable);

    private sealed class OperationComparer : IComparer<PbtWriteOperation<PbtPath>>
    {
        internal static readonly OperationComparer Instance = new();
        public int Compare(PbtWriteOperation<PbtPath> left, PbtWriteOperation<PbtPath> right) => left.Key.CompareTo(right.Key);
    }
}
