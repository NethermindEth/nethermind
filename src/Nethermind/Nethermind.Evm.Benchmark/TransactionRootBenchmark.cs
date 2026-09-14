// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Proofs;

namespace Nethermind.Evm.Benchmark;

[MemoryDiagnoser]
public class TransactionRootBenchmark
{
    private Transaction[] _transactions = null!;
    private Transaction[] _cachedTransactions = null!;
    private byte[][] _encoded = null!;

    [Params(1, 128, 200, 400, 4096)]
    public int Count { get; set; }

    [Params(0, 1024)]
    public int DataLength { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _transactions = new Transaction[Count];
        _cachedTransactions = new Transaction[Count];
        _encoded = new byte[Count][];
        byte[] data = new byte[DataLength];
        new System.Random(42).NextBytes(data);
        Signature signature = Build.A.Transaction.Signed().TestObject.Signature!;
        for (int i = 0; i < Count; i++)
        {
            Transaction transaction = Build.A.Transaction.WithNonce(i).WithType((TxType)(i % 3))
                .WithData(data).WithSignature(signature).TestObject;
            _transactions[i] = transaction;
            _encoded[i] = Rlp.Encode(transaction, RlpBehaviors.SkipTypedWrapping).Bytes;
            Transaction cached = Rlp.Decode<Transaction>(new Rlp(_encoded[i]), RlpBehaviors.SkipTypedWrapping | RlpBehaviors.ExcludeHashes);
            cached.SetPreHashMemoryNoLock(_encoded[i]);
            _cachedTransactions[i] = cached;
        }
    }

    [Benchmark]
    public Hash256 Uncached() => TxTrie.CalculateRoot(_transactions);

    [Benchmark]
    public Hash256 Cached() => TxTrie.CalculateRoot(_cachedTransactions);

    [Benchmark]
    public Hash256 Encoded() => TxTrie.CalculateRoot(_encoded);
}
