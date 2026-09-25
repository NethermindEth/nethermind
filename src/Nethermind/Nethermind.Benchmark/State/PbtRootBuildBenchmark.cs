// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using Nethermind.Pbt;
using Nethermind.State.Pbt;
using Nethermind.State.Pbt.Image;

namespace Nethermind.Benchmarks.State;

/// <summary>Computes the root of a whole tree from sorted leaves: the image verifier's streaming calculator against both updaters building it from empty.</summary>
/// <remarks>
/// The calculator only hashes, while the updaters also encode and publish every group, so the gap is the cost of
/// building the stored tree on top of the root. Each updater invocation starts from an empty store. Keys are
/// account-zone keys, the shape the partitioned driver the bucketing updater parallelizes through expects.
/// </remarks>
[MemoryDiagnoser]
public class PbtRootBuildBenchmark
{
    public enum Variant
    {
        /// <summary><see cref="PbtImageRootCalculator"/>, which the snapshot verifier checks an image's root with.</summary>
        ImageRootCalculator,
        /// <summary>The sorted-range updater, building every group from an empty tree.</summary>
        Sorted,
        /// <summary>The bucketing updater, building every group from an empty tree.</summary>
        Current,
        /// <summary>The sorted-range updater folding each frame's slots across threads, building every group from an empty tree.</summary>
        SortedParallel,
        /// <summary>The bucketing updater through the partitioned driver, folding buckets across threads, building every group from an empty tree.</summary>
        CurrentParallel,
        /// <summary>The sorted-range updater through the partitioned driver, sorting the shards and folding slots across threads, building every group from an empty tree.</summary>
        SortedPartitioned,
    }

    private PbtOverlayStore _store = null!;
    private readonly ConcurrencyController _foldQuota = new(Environment.ProcessorCount);
    private RebuildEntry[] _entries = null!;
    private PbtWriteOperation<PbtStorageTreeKey>[] _operations = null!;
    /// <summary>The leaves as account operations grouped by zone shard and shuffled within each, as the batch builder leaves them.</summary>
    private PbtWriteOperation<PbtPath>[] _shardedOperations = null!;
    private int[] _table = null!;
    private int[] _zoneTable = null!;

    [Params(10_000, 100_000, 1_000_000)]
    public int LeafCount { get; set; }

    [Params(Variant.ImageRootCalculator, Variant.Sorted, Variant.Current, Variant.SortedParallel, Variant.CurrentParallel, Variant.SortedPartitioned)]
    public Variant Method { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        Random random = new(8297);
        SortedDictionary<PbtStorageTreeKey, ValueHash256> leaves = [];
        Span<byte> bytes = stackalloc byte[PbtPath.KeyLength];
        Span<byte> value = stackalloc byte[32];
        while (leaves.Count < LeafCount)
        {
            random.NextBytes(bytes);
            bytes[0] = Eip8297KeyDerivation.AccountZone;
            random.NextBytes(value);
            value[0] |= 1;
            leaves[new PbtStorageTreeKey(bytes)] = new ValueHash256(value);
        }

        _entries = new RebuildEntry[LeafCount];
        _operations = new PbtWriteOperation<PbtStorageTreeKey>[LeafCount];
        PbtWriteOperation<PbtPath>[] accountOperations = new PbtWriteOperation<PbtPath>[LeafCount];
        int index = 0;
        foreach ((PbtStorageTreeKey key, ValueHash256 leaf) in leaves)
        {
            _entries[index] = new RebuildEntry(key, leaf);
            accountOperations[index] = new(new PbtPath(key.Bytes), leaf);
            _operations[index++] = new(key, leaf);
        }
        _table = PbtTrieUpdaterBenchmark.ShardTable<PbtStorageTreeKey>(_operations, 0);
        _zoneTable = PbtTrieUpdaterBenchmark.ShardTable<PbtPath>(accountOperations, PbtTrieUpdaterBenchmark.ZoneShardNibbleIndex);
        random.Shuffle(accountOperations);
        _shardedOperations = PbtTrieUpdaterBenchmark.GroupByShard(accountOperations, PbtTrieUpdaterBenchmark.ZoneShardNibbleIndex);
        _store = new PbtOverlayStore();

        ValueHash256 expected = PbtImageRootCalculator.Calculate(_entries, CancellationToken.None);
        foreach (Variant variant in Enum.GetValues<Variant>())
        {
            if (Compute(variant) != expected) throw new InvalidOperationException($"{variant} computes a different root.");
        }
    }

    [GlobalCleanup]
    public void GlobalCleanup() => _store.Dispose();

    [Benchmark]
    public ValueHash256 BuildRoot() => Compute(Method);

    private ValueHash256 Compute(Variant variant)
    {
        _store.ResetOverlay();
        return variant switch
        {
            Variant.ImageRootCalculator => PbtImageRootCalculator.Calculate(_entries, CancellationToken.None),
            Variant.Sorted => TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.UpdateRootSorted(_store, default, _operations, PbtPrefixlessBranchOmission.Interior),
            Variant.SortedParallel => TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.UpdateRootSorted(_store, default, _operations.AsMemory(),
                PbtPrefixlessBranchOmission.Interior, _foldQuota, FoldFanOut.Default),
            Variant.CurrentParallel => BuildPartitioned(sortedZoneFold: false),
            Variant.SortedPartitioned => BuildPartitioned(sortedZoneFold: true),
            _ => TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.UpdateRoot(_store, default,
                new PbtWriteBatch<PbtStorageTreeKey>(new ArrayPoolList<PbtWriteOperation<PbtStorageTreeKey>>(_operations), new ArrayPoolList<int>(_table), 0),
                PbtPrefixlessBranchOmission.Interior),
        };
    }

    private ValueHash256 BuildPartitioned(bool sortedZoneFold)
    {
        using PbtPartitionBatches batches = new()
        {
            Account = new PbtWriteBatch<PbtPath>(new ArrayPoolList<PbtWriteOperation<PbtPath>>(_shardedOperations), new ArrayPoolList<int>(_zoneTable),
                PbtTrieUpdaterBenchmark.ZoneShardNibbleIndex),
        };
        return TrieUpdater.UpdateRoot(_store, default, batches, _foldQuota, FoldFanOut.Default, PbtPrefixlessBranchOmission.Interior, sortedZoneFold, null);
    }
}
