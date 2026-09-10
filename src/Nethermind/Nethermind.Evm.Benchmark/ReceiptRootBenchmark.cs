// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using Nethermind.State.Proofs;

namespace Nethermind.Evm.Benchmark;

[MemoryDiagnoser]
public class ReceiptRootBenchmark
{
    private readonly ReceiptMessageDecoder _decoder = new();
    private TxReceipt[] _receipts = null!;

    [Params(1, 128, 1024, 4096)]
    public int Count { get; set; }

    [Params(0, 1024)]
    public int LogDataLength { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _receipts = new TxReceipt[Count];
        byte[] data = new byte[LogDataLength];
        new System.Random(42).NextBytes(data);
        for (int i = 0; i < Count; i++)
        {
            _receipts[i] = Build.A.Receipt.WithAllFieldsFilled.WithGasUsedTotal((ulong)(i + 1) * 21000)
                .WithTxType((TxType)(i % 5))
                .WithLogs(new LogEntry(TestItem.AddressA, data, [TestItem.KeccakA, TestItem.KeccakB])).TestObject;
        }
    }

    [Benchmark]
    public Hash256 CalculateRoot() => ReceiptTrie.CalculateRoot(Osaka.Instance, _receipts, _decoder);
}
