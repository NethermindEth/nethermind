// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;
using Nethermind.State.Pbt;
using Nethermind.State.Pbt.Image;

namespace Nethermind.Benchmarks.State;

/// <summary>Computes the root of a whole tree from sorted leaves: the image verifier's streaming calculator against both updaters building it from empty.</summary>
/// <remarks>
/// The calculator only hashes, while the updaters also encode and publish every group, so the gap is the cost of
/// building the stored tree on top of the root. Each updater invocation starts from an empty store.
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
    }

    private PbtOverlayStore _store = null!;
    private RebuildEntry[] _entries = null!;
    private PbtWriteOperation<PbtStorageTreeKey>[] _operations = null!;
    private int[] _table = null!;

    [Params(10_000, 100_000, 1_000_000)]
    public int LeafCount { get; set; }

    [Params(Variant.ImageRootCalculator, Variant.Sorted, Variant.Current)]
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
            random.NextBytes(value);
            value[0] |= 1;
            leaves[new PbtStorageTreeKey(bytes)] = new ValueHash256(value);
        }

        _entries = new RebuildEntry[LeafCount];
        _operations = new PbtWriteOperation<PbtStorageTreeKey>[LeafCount];
        int[] counts = new int[16];
        int index = 0;
        foreach ((PbtStorageTreeKey key, ValueHash256 leaf) in leaves)
        {
            _entries[index] = new RebuildEntry(key, leaf);
            _operations[index++] = new(key, leaf);
            counts[key.Bytes[0] >> 4]++;
        }

        // The shard table PbtWriteBatchBuilder builds at nibble zero: the used-shard mask, then each used shard's count.
        List<int> table = [0];
        for (int shard = 0; shard < counts.Length; shard++)
        {
            if (counts[shard] == 0) continue;
            table[0] |= 1 << shard;
            table.Add(counts[shard]);
        }
        _table = [.. table];
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
            _ => TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.UpdateRoot(_store, default,
                new PbtWriteBatch<PbtStorageTreeKey>(new ArrayPoolList<PbtWriteOperation<PbtStorageTreeKey>>(_operations), new ArrayPoolList<int>(_table), 0),
                PbtPrefixlessBranchOmission.Interior),
        };
    }
}
