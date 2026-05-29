// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;
using Nethermind.Trie.Sparse;

namespace Nethermind.Trie.Benchmark;

[MemoryDiagnoser]
public class ProofReaderBenchmarks
{
    [Params(1, 10, 100, 1_000)]
    public int N;

    private MemDb _db = null!;
    private Hash256 _rootHash;
    private Hash256[] _targets = null!;
    private HalfPathTrieNodeReader _reader = null!;

    [GlobalSetup]
    public void Setup()
    {
        _db = new MemDb();
        PatriciaTree tree = new(new RawTrieStore(_db).GetTrieStore(null), LimboLogs.Instance);
        Random rng = new(42);
        for (int i = 0; i < 10_000; i++)
        {
            byte[] key = new byte[32];
            rng.NextBytes(key);
            tree.Set(Keccak.Compute(key).Bytes, TestItem.GenerateIndexedAccountRlp(i));
        }
        tree.UpdateRootHash();
        tree.Commit();
        _rootHash = tree.RootHash;

        _targets = new Hash256[N];
        rng = new(123);
        for (int i = 0; i < N; i++)
        {
            byte[] key = new byte[32];
            rng.NextBytes(key);
            _targets[i] = Keccak.Compute(key);
        }
        _reader = new HalfPathTrieNodeReader(new NodeStorage(_db));
    }

    [Benchmark]
    public DecodedMultiProof ReadProofs() =>
        MultiProofReader.ReadAccountProofs(_reader, _rootHash, _targets);
}

[MemoryDiagnoser]
public class SparseTrieUpdateBenchmarks
{
    [Params(10, 100, 1_000)]
    public int N;

    private Dictionary<ValueHash256, LeafUpdate> _updates = null!;
    private SparsePatriciaTree _prePopulated = null!;

    [GlobalSetup]
    public void Setup()
    {
        Random rng = new(42);
        _updates = [];
        for (int i = 0; i < N; i++)
        {
            byte[] key = new byte[32];
            rng.NextBytes(key);
            byte[] value = TestItem.GenerateIndexedAccountRlp(i);
            _updates[Keccak.Compute(key)] = LeafUpdate.Changed(value);
        }

        _prePopulated = new SparsePatriciaTree();
        _prePopulated.UpdateLeaves(_updates, null);
        _prePopulated.ComputeRoot();
    }

    [Benchmark]
    public Hash256 UpdateAndComputeRoot()
    {
        Random rng = new(123);
        Dictionary<ValueHash256, LeafUpdate> changes = [];
        foreach (Hash256 key in _updates.Keys)
        {
            if (rng.Next(100) < 70)
            {
                byte[] value = TestItem.GenerateIndexedAccountRlp(rng.Next(10000));
                changes[key] = LeafUpdate.Changed(value);
            }
        }
        _prePopulated.UpdateLeaves(changes, null);
        return _prePopulated.ComputeRoot();
    }

    [GlobalCleanup]
    public void Cleanup() => _prePopulated?.Dispose();
}

[MemoryDiagnoser]
public class EndToEndBenchmarks
{
    [Params(10, 100, 1_000)]
    public int N;

    private Hash256[] _keys = null!;
    private byte[][] _values = null!;
    private MemDb _db = null!;

    [GlobalSetup]
    public void Setup()
    {
        Random rng = new(42);
        _keys = new Hash256[N];
        _values = new byte[N][];
        for (int i = 0; i < N; i++)
        {
            byte[] key = new byte[32];
            rng.NextBytes(key);
            _keys[i] = Keccak.Compute(key);
            _values[i] = TestItem.GenerateIndexedAccountRlp(i);
        }
        _db = new MemDb();
    }

    [Benchmark(Baseline = true)]
    public Hash256 Patricia_BulkSetAndRoot()
    {
        MemDb freshDb = new();
        PatriciaTree tree = new(new RawTrieStore(freshDb).GetTrieStore(null), LimboLogs.Instance);
        for (int i = 0; i < N; i++)
            tree.Set(_keys[i].Bytes, _values[i]);
        tree.UpdateRootHash();
        tree.Commit();
        return tree.RootHash;
    }

    [Benchmark]
    public Hash256 Sparse_UpdateAndRoot()
    {
        using SparsePatriciaTree sparse = new();
        Dictionary<ValueHash256, LeafUpdate> updates = new(N);
        for (int i = 0; i < N; i++)
            updates[_keys[i]] = LeafUpdate.Changed(_values[i]);
        sparse.UpdateLeaves(updates, null);
        return sparse.ComputeRoot();
    }
}
