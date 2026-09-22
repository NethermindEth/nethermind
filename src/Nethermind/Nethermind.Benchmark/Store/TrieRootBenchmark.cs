// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Benchmarks.Store;

[MemoryDiagnoser]
public class TrieRootBenchmark
{
    private MemDb _db;
    private RawScopedTrieStore _store;
    private Hash256 _root;
    private byte[][] _keys;
    private PatriciaTree _tree;

    [Params(64, 1024, 8192)]
    public int ChangedAccounts { get; set; }

    [Params(false, true)]
    public bool Clustered { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _db = new MemDb();
        _store = new RawScopedTrieStore(_db);
        PatriciaTree tree = new(_store, NullLogManager.Instance);
        Random random = new(42);
        _keys = new byte[ChangedAccounts][];
        int changed = 0;
        byte[] value = new byte[80];
        random.NextBytes(value);
        for (int i = 0; i < 131072; i++)
        {
            byte[] key = new byte[32];
            random.NextBytes(key);
            tree.Set(key, value);
            if (changed < _keys.Length && (!Clustered || key[0] < 64))
            {
                _keys[changed++] = key;
            }
        }
        tree.Commit();
        _root = tree.RootHash;
    }

    [IterationSetup]
    public void PrepareChanges()
    {
        _tree = new PatriciaTree(_store, NullLogManager.Instance) { RootHash = _root };
        byte[] value = new byte[80];
        Array.Fill(value, (byte)0x5a);
        foreach (byte[] key in _keys) _tree.Set(key, value);
    }

    [Benchmark(Baseline = true)]
    public Hash256 RootChildren()
    {
        TreePath path = TreePath.Empty;
        _tree.RootRef.ResolveKey(_store, ref path);
        return _tree.RootRef.Keccak;
    }

    [Benchmark]
    public Hash256 TwoNibbleSubtries()
    {
        _tree.UpdateRootHash();
        return _tree.RootHash;
    }

    [GlobalCleanup]
    public void Cleanup() => _db.Dispose();
}
