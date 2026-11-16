// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain.Blocks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Proofs;

namespace Nethermind.Network.Benchmarks;

/// <summary>
/// Measures what old bodies sync does with one received body: decode it, check its transaction root and store it.
/// </summary>
[MemoryDiagnoser]
public class BlockBodyStoreBenchmarks
{
    private BlockHeader _header = null!;
    private byte[] _body = null!;
    private IContainer _container = null!;
    private IBlockStore _store = null!;

    [Params(150, 500)]
    public int TransactionCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        Transaction[] transactions = new Transaction[TransactionCount];
        for (int i = 0; i < transactions.Length; i++)
        {
            transactions[i] = Build.A.Transaction
                .WithType(i % 3 == 0 ? TxType.Legacy : TxType.EIP1559)
                .WithNonce((ulong)i)
                .WithData(new byte[200])
                .Signed()
                .TestObject;
        }

        Block block = Build.A.Block.WithNumber(1).WithTransactions(transactions).TestObject;
        _header = block.Header;
        _body = BlockBodyDecoder.Instance.Encode(block.Body);
        _container = new ContainerBuilder().AddModule(new TestNethermindModule()).Build();
        _store = _container.Resolve<IBlockStore>();
    }

    [GlobalCleanup]
    public void Cleanup() => _container.Dispose();

    [Benchmark(Baseline = true)]
    public Hash256 Decoded()
    {
        RlpReader reader = new(_body);
        BlockBody body = BlockBodyDecoder.Instance.Decode(ref reader)!;
        Hash256 root = TxTrie.CalculateRoot(body.Transactions);
        _store.Insert(new Block(_header, body));
        foreach (Transaction transaction in body.Transactions) TxDecoder.TxObjectPool.Return(transaction);
        return root;
    }

    [Benchmark]
    public Hash256 Encoded()
    {
        BlockBody body = BlockBodyDecoder.Instance.DecodeWithEncodedTransactions(_body)!;
        Hash256 root = TxTrie.CalculateRoot(body.Transactions);
        _store.Insert(_header, _body);
        foreach (Transaction transaction in body.Transactions) TxDecoder.TxObjectPool.Return(transaction);
        return root;
    }
}
