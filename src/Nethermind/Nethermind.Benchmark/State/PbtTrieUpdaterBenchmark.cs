// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;

namespace Nethermind.Benchmarks.State;

/// <summary>Compares the bucketing <c>TrieUpdater.UpdateRoot</c> with the sorted-range <c>UpdateRootSorted</c> on one thread.</summary>
/// <remarks>
/// Every invocation applies one of <see cref="BatchVariants"/> prepared batches to the same base tree: the store keeps the
/// base groups apart and discards what the previous invocation wrote, so no invocation sees another's changes.
/// </remarks>
[MemoryDiagnoser]
public class PbtTrieUpdaterBenchmark
{
    public enum Variant
    {
        /// <summary>The bucketing updater, given the sorted batch and the shard table the builder produces.</summary>
        Current,
        /// <summary>The sorted-range updater, given the sorted batch.</summary>
        Sorted,
        /// <summary>The sorted-range updater, sorting a shuffled copy of the batch first.</summary>
        SortedIncludingSort,
    }

    private const int BatchVariants = 64;
    private const int BuildChunk = 1 << 16;

    private PbtOverlayStore _store = null!;
    private ValueHash256 _root;
    private Batch[] _batches = null!;
    private int _next;

    [Params(100_000, 1_000_000)]
    public int TreeSize { get; set; }

    [Params(1, 16, 256, 4096)]
    public int BatchSize { get; set; }

    [Params(Variant.Current, Variant.Sorted, Variant.SortedIncludingSort)]
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
            case Variant.Current:
                return TrieUpdater<PbtPath, PbtNodePath>.UpdateRoot(_store, _root,
                    new PbtWriteBatch<PbtPath>(new ArrayPoolList<PbtWriteOperation<PbtPath>>(batch.Sorted), new ArrayPoolList<int>(batch.Table), 0),
                    PbtPrefixlessBranchOmission.Interior);
            case Variant.Sorted:
                {
                    using ArrayPoolList<PbtWriteOperation<PbtPath>> operations = new(batch.Sorted);
                    return TrieUpdater<PbtPath, PbtNodePath>.UpdateRootSorted(_store, _root, operations.AsSpan(), PbtPrefixlessBranchOmission.Interior);
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

        // The shard table PbtWriteBatchBuilder builds at nibble zero: the used-shard mask, then each used shard's count.
        int[] counts = new int[16];
        foreach (PbtWriteOperation<PbtPath> operation in sorted) counts[operation.Key.Bytes[0] >> 4]++;
        List<int> table = [0];
        for (int shard = 0; shard < counts.Length; shard++)
        {
            if (counts[shard] == 0) continue;
            table[0] |= 1 << shard;
            table.Add(counts[shard]);
        }
        return new Batch(sorted, shuffled, [.. table]);
    }

    private static PbtPath RandomKey(Random random)
    {
        Span<byte> bytes = stackalloc byte[PbtPath.KeyLength];
        random.NextBytes(bytes);
        return new PbtPath(bytes);
    }

    private static ValueHash256 RandomValue(Random random)
    {
        Span<byte> bytes = stackalloc byte[32];
        random.NextBytes(bytes);
        bytes[0] |= 1;
        return new ValueHash256(bytes);
    }

    private sealed record Batch(PbtWriteOperation<PbtPath>[] Sorted, PbtWriteOperation<PbtPath>[] Shuffled, int[] Table);

    private sealed class OperationComparer : IComparer<PbtWriteOperation<PbtPath>>
    {
        internal static readonly OperationComparer Instance = new();
        public int Compare(PbtWriteOperation<PbtPath> left, PbtWriteOperation<PbtPath> right) => left.Key.CompareTo(right.Key);
    }
}
