// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using Nethermind.State.Proofs;

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
