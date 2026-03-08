// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Db;
using Nethermind.Trie;

namespace Nethermind.Trie.Benchmark;

/// <summary>
/// Measures PatriciaTree.Get() throughput across trie sizes.
///
/// Covers the hot path for every account and storage slot read in Nethermind.
/// Trie is fully in-memory (MemDb) so measurements reflect trie traversal overhead,
/// not I/O, isolating the cost of the trie traversal logic.
///
/// All keys are random 32-byte values (matching real Keccak-hashed keys).
/// 75% of benchmark iterations hit existing keys; 25% hit missing keys —
/// a realistic distribution for Ethereum reads (most reads find an account/slot,
/// some miss on first access).
/// </summary>
[MemoryDiagnoser]
public class PatriciaTreeGetBenchmark
{
    [Params(1_000, 10_000)]
    public int N { get; set; }

    private PatriciaTree _tree = null!;
    private byte[][] _existingKeys = null!;
    private byte[][] _missingKeys = null!;
    private int _existingIdx;
    private int _missingIdx;

    [GlobalSetup]
    public void Setup()
    {
        _tree = new PatriciaTree(new MemDb());

        Random rand = new(42);
        _existingKeys = new byte[N][];

        // Realistic 32-byte value (e.g. RLP-encoded account data)
        byte[] value = new byte[32];

        for (int i = 0; i < N; i++)
        {
            byte[] key = new byte[32];
            rand.NextBytes(key);
            rand.NextBytes(value);
            _tree.Set(key, value);
            _existingKeys[i] = key;
        }

        _tree.Commit();

        // Pre-generate missing keys (distinct from existing keys)
        _missingKeys = new byte[N / 4 + 1][];
        for (int i = 0; i < _missingKeys.Length; i++)
        {
            _missingKeys[i] = new byte[32];
            rand.NextBytes(_missingKeys[i]);
        }

        // Pre-warm all paths to exclude JIT from measurements
        for (int i = 0; i < N; i++) _tree.Get(_existingKeys[i]);
        for (int i = 0; i < _missingKeys.Length; i++) _tree.Get(_missingKeys[i]);
    }

    /// <summary>Lookup an existing key — the common case in block processing.</summary>
    [Benchmark(Baseline = true)]
    public ReadOnlySpan<byte> GetExistingKey()
    {
        int idx = _existingIdx % N;
        _existingIdx++;
        return _tree.Get(_existingKeys[idx]);
    }

    /// <summary>Lookup a missing key — accounts/slots not yet created.</summary>
    [Benchmark]
    public ReadOnlySpan<byte> GetMissingKey()
    {
        int idx = _missingIdx % _missingKeys.Length;
        _missingIdx++;
        return _tree.Get(_missingKeys[idx]);
    }
}
