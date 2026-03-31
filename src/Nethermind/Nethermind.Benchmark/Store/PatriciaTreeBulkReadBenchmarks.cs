// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Benchmarks.Store;

[MemoryDiagnoser]
public class PatriciaTreeBulkReadBenchmarks
{
    private const int TreeSize = 4096;

    private IScopedTrieStore _trieStore = null!;
    private PatriciaTree _tree = null!;
    private ValueHash256[] _readKeys = null!;

    [Params(16, 64, 256, 1024)]
    public int KeyCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        TestMemDb db = new();
        _trieStore = new RawScopedTrieStore(db);
        _tree = new PatriciaTree(_trieStore, LimboLogs.Instance);

        Random rng = new(42);
        ValueHash256[] allKeys = new ValueHash256[TreeSize];

        using ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries = new(TreeSize);
        for (int i = 0; i < TreeSize; i++)
        {
            byte[] keyBuffer = new byte[32];
            rng.NextBytes(keyBuffer);
            allKeys[i] = new ValueHash256(keyBuffer);

            byte[] valueBuffer = new byte[32];
            rng.NextBytes(valueBuffer);
            entries.Add(new PatriciaTree.BulkSetEntry(in allKeys[i], valueBuffer));
        }

        _tree.BulkSet(entries);
        _tree.Commit();

        // Select KeyCount keys to read (from the inserted keys)
        _readKeys = new ValueHash256[KeyCount];
        Random readRng = new(123);
        for (int i = 0; i < KeyCount; i++)
        {
            _readKeys[i] = allKeys[readRng.Next(TreeSize)];
        }
    }

    [Benchmark(Baseline = true)]
    public void ReadOneByOne()
    {
        for (int i = 0; i < _readKeys.Length; i++)
        {
            _tree.Get(_readKeys[i].Bytes);
        }
    }

    [Benchmark]
    public void ParallelReadOneByOne()
    {
        ValueHash256[] keys = _readKeys;
        PatriciaTree tree = _tree;
        Parallel.For(0, keys.Length, (i) =>
        {
            tree.Get(keys[i].Bytes);
        });
    }

    [Benchmark]
    public void BulkRead()
    {
        NoOpSink sink = default;
        PatriciaTrieBulkReader.BulkRead(_trieStore, _tree.RootRef, _readKeys, ref sink);
    }

    private struct NoOpSink : IPatriciaTrieBulkReaderSink<NoOpSink>
    {
        public void OnRead(in ValueHash256 key, int idx, ReadOnlySpan<byte> value) { }
    }
}
