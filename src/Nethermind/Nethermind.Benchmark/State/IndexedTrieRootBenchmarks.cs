// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using Nethermind.State.Proofs;
using Nethermind.Trie;

namespace Nethermind.Benchmarks.State;

public class IndexedTrieRootBenchmarks
{
    public enum LengthMix
    {
        Mixed,
        Short,
        Long
    }

    private static readonly ReceiptMessageDecoder Decoder = new();
    private byte[][] _transactions = null!;
    private TxReceipt[] _receipts = null!;

    [Params(24, 64, 200)]
    public int Count { get; set; }

    [Params(LengthMix.Mixed, LengthMix.Short, LengthMix.Long)]
    public LengthMix Mix { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        VerifyRoots();
        Random random = new(Count * 3 + (int)Mix);
        _transactions = new byte[Count][];
        _receipts = new TxReceipt[Count];
        for (int i = 0; i < Count; i++)
        {
            byte[] transaction = new byte[GetTransactionLength(random)];
            random.NextBytes(transaction);
            transaction[0] = 2;
            _transactions[i] = transaction;

            LogEntry[] logs = new LogEntry[GetLogCount(random)];
            for (int j = 0; j < logs.Length; j++)
            {
                byte[] data = new byte[random.Next(0, 3) * 32];
                random.NextBytes(data);
                logs[j] = new LogEntry(new Address(RandomBytes(random, Address.Size)), data,
                    [new Hash256(RandomBytes(random, Hash256.Size)), new Hash256(RandomBytes(random, Hash256.Size))]);
            }
            _receipts[i] = new TxReceipt
            {
                TxType = TxType.EIP1559,
                StatusCode = 1,
                GasUsedTotal = (ulong)(i + 1) * 50_000,
                Bloom = new Bloom(RandomBytes(random, Bloom.ByteLength)),
                Logs = logs,
            };
        }
    }

    [Benchmark]
    public Hash256 TxRoot() => TxTrie.CalculateRoot(_transactions);

    [Benchmark]
    public Hash256 ReceiptRoot() => ReceiptTrie.CalculateRoot(Osaka.Instance, _receipts, Decoder);

    private static readonly int[] VerifiedLengths = [1, 2, 31, 32, 100, 123, 124, 125, 136, 261, 262, 300, 2163, 2164, 2165, 4000];

    private static void VerifyRoots()
    {
        Random random = new(7);
        for (int count = 1; count <= 300; count++)
        {
            byte[][] values = new byte[count][];
            using TrackingCappedArrayPool pool = new();
            TxTrie trie = new(ReadOnlySpan<Transaction>.Empty, bufferPool: pool, canBeParallel: false);
            for (int i = 0; i < count; i++)
            {
                byte[] value = new byte[VerifiedLengths[random.Next(VerifiedLengths.Length)]];
                random.NextBytes(value);
                values[i] = value;
                trie.Set(Nethermind.Serialization.Rlp.Rlp.Encode(i), value);
            }
            trie.UpdateRootHash(canBeParallel: false);
            if (TxTrie.CalculateRoot(values) != trie.RootHash) throw new InvalidOperationException($"Transaction root mismatch at {count}");

            TxReceipt[] receipts = new TxReceipt[count];
            for (int i = 0; i < count; i++)
            {
                LogEntry[] logs = new LogEntry[random.Next(4) == 0 ? random.Next(10, 30) : random.Next(0, 4)];
                for (int j = 0; j < logs.Length; j++)
                    logs[j] = new LogEntry(new Address(RandomBytes(random, Address.Size)), RandomBytes(random, random.Next(0, 3) * 32),
                        [new Hash256(RandomBytes(random, Hash256.Size))]);
                receipts[i] = new TxReceipt { TxType = TxType.EIP1559, StatusCode = 1, GasUsedTotal = (ulong)i + 1, Bloom = new Bloom(RandomBytes(random, Bloom.ByteLength)), Logs = logs };
            }
            using TrackingCappedArrayPool receiptPool = new();
            if (ReceiptTrie.CalculateRoot(Osaka.Instance, receipts, Decoder) != new ReceiptTrie(Osaka.Instance, receipts, Decoder, receiptPool, canBeParallel: false).RootHash)
                throw new InvalidOperationException($"Receipt root mismatch at {count}");
        }
    }

    private int GetTransactionLength(Random random) => Mix switch
    {
        LengthMix.Short => random.Next(100, 125),
        LengthMix.Long => random.Next(125, 700),
        _ => random.Next(100) switch
        {
            < 40 => random.Next(100, 125),
            < 80 => random.Next(125, 700),
            < 95 => random.Next(700, 2100),
            _ => random.Next(2200, 6000),
        },
    };

    private int GetLogCount(Random random) => Mix switch
    {
        LengthMix.Short => 0,
        LengthMix.Long => random.Next(1, 5),
        _ => random.Next(100) < 85 ? random.Next(0, 5) : random.Next(14, 30),
    };

    private static byte[] RandomBytes(Random random, int length)
    {
        byte[] bytes = new byte[length];
        random.NextBytes(bytes);
        return bytes;
    }
}
