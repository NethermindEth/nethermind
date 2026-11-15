// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Benchmarks.Rlp;

public class RlpEncodeReceiptBenchmark
{
    private const int Count = 200;
    private const RlpBehaviors TrieBehaviors = RlpBehaviors.Eip658Receipts | RlpBehaviors.SkipTypedWrapping;

    private static readonly ReceiptMessageDecoder Decoder = new();
    private TxReceipt[] _receipts = null!;
    private byte[] _buffer = null!;

    [Params(0, 2, 8)]
    public int Logs { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        Random random = new(Logs);
        _receipts = new TxReceipt[Count];
        for (int i = 0; i < Count; i++)
        {
            LogEntry[] logs = new LogEntry[Logs];
            for (int j = 0; j < logs.Length; j++)
            {
                byte[] data = new byte[random.Next(0, 3) * 32];
                random.NextBytes(data);
                logs[j] = new LogEntry(new Address(RandomBytes(random, Address.Size)), data,
                    [new Hash256(RandomBytes(random, Hash256.Size)), new Hash256(RandomBytes(random, Hash256.Size))]);
            }
            _receipts[i] = new TxReceipt
            {
                TxType = i % 4 == 0 ? TxType.Legacy : TxType.EIP1559,
                StatusCode = 1,
                GasUsedTotal = (ulong)(i + 1) * 50_000,
                Bloom = new Bloom(RandomBytes(random, Bloom.ByteLength)),
                Logs = logs,
            };
        }
        _buffer = new byte[1 << 16];
    }

    [Benchmark]
    public int TrieEncode()
    {
        int total = 0;
        foreach (TxReceipt receipt in _receipts)
        {
            int length = Decoder.GetLength(receipt, TrieBehaviors);
            RlpWriter writer = RlpWriter.ContentWriter(_buffer.AsSpan(0, length));
            Decoder.Encode(ref writer, receipt, TrieBehaviors);
            total += writer.Length;
        }
        return total;
    }

    private static byte[] RandomBytes(Random random, int length)
    {
        byte[] bytes = new byte[length];
        random.NextBytes(bytes);
        return bytes;
    }
}
