// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Collections;
using Nethermind.Trie;

namespace Nethermind.Benchmarks.State;

/// <summary>Cost of building the negative filters of one committed block snapshot, split by phase.</summary>
/// <remarks>
/// Sizes follow a fusaka block: ~10k entries per mutable snapshot (EXPB layer probe), ~1.5k account and ~1k slot writes
/// (Slow block logs); the trie-node split is estimated.
/// </remarks>
[MemoryDiagnoser]
public class SnapshotFilterBuildBenchmark
{
    private const int Accounts = 1_500;
    private const int Slots = 1_000;
    private const int StateNodes = 3_500;
    private const int StorageNodes = 4_000;
    private const int StorageAddresses = 300;

    private SnapshotContent _content = null!;
    private SnapshotContentCounts _counts;
    private uint[] _hashes = null!;
    private readonly SplitBlockFilter _reused = new();

    [GlobalSetup]
    public void Setup()
    {
        _content = new SnapshotContent();
        for (int i = 0; i < Accounts; i++) _content.Accounts[new(Address(i))] = new Account((ulong)i, 1);
        for (int i = 0; i < Slots; i++) _content.Storages[new((Address(i % StorageAddresses), (UInt256)i))] = (UInt256)i;
        for (int i = 0; i < StateNodes; i++)
        {
            Hash256 hash = Keccak.Compute($"s{i}");
            _content.StateNodes[new(new TreePath(hash, i % 64))] = new TrieNode(NodeType.Branch, hash);
        }

        for (int i = 0; i < StorageNodes; i++)
        {
            Hash256 hash = Keccak.Compute($"n{i}");
            _content.StorageNodes[(Keccak.Compute($"a{i % StorageAddresses}"), new TreePath(hash, i % 64))] = new TrieNode(NodeType.Branch, hash);
        }

        _counts = _content.CaptureCounts();
        _hashes = new uint[Accounts + Slots + StateNodes + StorageNodes];
        for (int i = 0; i < _hashes.Length; i++) _hashes[i] = (uint)Keccak.Compute($"h{i}").GetHashCode();
        _reused.Reset(_hashes.Length);
    }

    private static Address Address(int i) => new(Keccak.Compute($"a{i}").Bytes[..20]);

    [Benchmark(Baseline = true)]
    public object BuildFresh()
    {
        SnapshotContentFilters filters = new();
        filters.Build(_content, _counts);
        return filters;
    }

    [Benchmark]
    public void BuildPooled() => _content.Filters.Build(_content, _counts);

    [Benchmark]
    public uint EnumerateAccounts()
    {
        uint x = 0;
        foreach (KeyValuePair<HashedKey<Address>, Account?> entry in _content.Accounts) x ^= (uint)entry.Key.GetHashCode();
        return x;
    }

    [Benchmark]
    public uint EnumerateStorages()
    {
        uint x = 0;
        foreach (KeyValuePair<HashedKey<(Address, UInt256)>, UInt256?> entry in _content.Storages) x ^= (uint)entry.Key.GetHashCode();
        return x;
    }

    [Benchmark]
    public uint EnumerateStateNodes()
    {
        uint x = 0;
        foreach (KeyValuePair<HashedKey<TreePath>, TrieNode> entry in _content.StateNodes) x ^= (uint)entry.Key.GetHashCode();
        return x;
    }

    [Benchmark]
    public uint EnumerateStorageNodes()
    {
        uint x = 0;
        foreach (KeyValuePair<HashedKey<(Hash256, TreePath)>, TrieNode> entry in _content.StorageNodes) x ^= (uint)entry.Key.GetHashCode();
        return x;
    }

    [Benchmark]
    public void AddStorageNodeKeyHashes()
    {
        _reused.Reset(StorageNodes);
        _content.StorageNodes.AddKeyHashesTo(_reused);
    }

    [Benchmark]
    public void ResetAndAddOnly()
    {
        _reused.Reset(_hashes.Length);
        foreach (uint hash in _hashes) _reused.Add(hash);
    }

    [Benchmark]
    public object AllocateFilters()
    {
        SplitBlockFilter[] filters = [new(), new(), new(), new()];
        filters[0].Reset(Accounts);
        filters[1].Reset(Slots);
        filters[2].Reset(StateNodes);
        filters[3].Reset(StorageNodes);
        return filters;
    }
}
