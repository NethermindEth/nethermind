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

/// <summary>Computes the root of a whole tree from sorted leaves: the image verifier's streaming calculator against the updater building it from empty.</summary>
/// <remarks>
/// The calculator only hashes, while the updater also encodes and publish every group, so the gap is the cost of
/// building the stored tree on top of the root. Each updater invocation starts from an empty store. Keys are
/// account-zone keys, the shape the partitioned driver expects.
/// </remarks>
[MemoryDiagnoser]
public class PbtRootBuildBenchmark
{
    public enum Variant
    {
        /// <summary><see cref="PbtImageRootCalculator"/>, which the snapshot verifier checks an image's root with.</summary>
        ImageRootCalculator,
        /// <summary>The partitioned updater, sorting the shards and folding slots across threads, building every group from an empty tree.</summary>
        Partitioned,
    }

    private PbtOverlayStore _store = null!;
    private readonly ConcurrencyController _foldQuota = new(Environment.ProcessorCount);
    private RebuildEntry[] _entries = null!;
    /// <summary>The leaves as account operations grouped by zone shard and shuffled within each, as the batch builder leaves them.</summary>
    private PbtWriteOperation<PbtPath>[] _shardedOperations = null!;
    private int[] _zoneTable = null!;

    [Params(10_000, 100_000, 1_000_000)]
    public int LeafCount { get; set; }

    [Params(Variant.ImageRootCalculator, Variant.Partitioned)]
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
        PbtWriteOperation<PbtPath>[] accountOperations = new PbtWriteOperation<PbtPath>[LeafCount];
        int index = 0;
        foreach ((PbtStorageTreeKey key, ValueHash256 leaf) in leaves)
        {
            _entries[index] = new RebuildEntry(key, leaf);
            accountOperations[index++] = new(new PbtPath(key.Bytes), leaf);
        }
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
            _ => BuildPartitioned(),
        };
    }

    private ValueHash256 BuildPartitioned()
    {
        using PbtPartitionBatches batches = new()
        {
            Account = new PbtWriteBatch<PbtPath>(new ArrayPoolList<PbtWriteOperation<PbtPath>>(_shardedOperations), new ArrayPoolList<int>(_zoneTable),
                PbtTrieUpdaterBenchmark.ZoneShardNibbleIndex),
        };
        return TrieUpdater.UpdateRoot(_store, default, batches, _foldQuota, FoldFanOut.Default, PbtPrefixlessBranchOmission.Interior, null);
    }
}
