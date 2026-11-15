// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.State.Proofs;
using Nethermind.Trie;

namespace Nethermind.Benchmarks.State;

[MemoryDiagnoser]
[WarmupCount(5)]
[MinIterationCount(15)]
[MaxIterationCount(30)]
public class IndexedTrieRootPaddedLengthBenchmarks
{
    private const int KeccakRate = 136;
    private byte[][] _transactions = null!;

    [Params(17, 18, 19)]
    public int Count { get; set; }

    [Params(2, 16)]
    public int RateBlocks { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        Random random = new(Count * 31 + RateBlocks);
        _transactions = new byte[Count][];
        for (int i = 0; i < Count; i++)
        {
            byte[] transaction = new byte[random.Next(RateBlocks * KeccakRate - 120, RateBlocks * KeccakRate - 21)];
            random.NextBytes(transaction);
            transaction[0] = 2;
            _transactions[i] = transaction;
        }

        using TrackingCappedArrayPool pool = new();
        TxTrie trie = new(ReadOnlySpan<Transaction>.Empty, bufferPool: pool, canBeParallel: false);
        for (int i = 0; i < Count; i++)
            trie.Set(Nethermind.Serialization.Rlp.Rlp.Encode(i), _transactions[i]);
        trie.UpdateRootHash(canBeParallel: false);
        if (TxRoot() != trie.RootHash) throw new InvalidOperationException($"Transaction root mismatch at {Count} x {RateBlocks} blocks");
    }

    [Benchmark]
    public Hash256 TxRoot() => TxTrie.CalculateRoot(_transactions);
}
