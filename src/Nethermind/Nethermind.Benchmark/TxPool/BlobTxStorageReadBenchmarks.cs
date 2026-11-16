// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using BenchmarkDotNet.Attributes;
using Nethermind.Api;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.TxPool;

namespace Nethermind.Benchmarks.TxPool;

/// <summary>Reading stored blob transactions from a RocksDB-backed <see cref="BlobTxStorage"/>, with and without their blobs.</summary>
[MemoryDiagnoser]
public class BlobTxStorageReadBenchmarks
{
    private const int TransactionCount = 16;

    private TempPath _directory = null!;
    private IContainer _container = null!;
    private BlobTxStorage _storage = null!;
    private Transaction[] _transactions = null!;

    [Params(1, 6)]
    public int BlobsPerTransaction { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _directory = TempPath.GetTempDirectory();
        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new InitConfig { BaseDbPath = _directory.Path }, new TxPoolConfig()))
            // The test modules keep every database in memory; put them back on RocksDB.
            .AddSingleton<IDbFactory, RocksDbFactory>()
            .Build();
        _storage = (BlobTxStorage)_container.Resolve<IBlobTxStorage>();

        Transaction template = Build.A.Transaction.WithShardBlobTxTypeAndFields(BlobsPerTransaction, spec: Osaka.Instance).TestObject;
        EthereumEcdsa ecdsa = new(BlockchainIds.Mainnet);
        _transactions = new Transaction[TransactionCount];
        for (int i = 0; i < TransactionCount; i++)
        {
            Transaction tx = Build.A.Transaction
                .With(t =>
                {
                    t.Type = TxType.Blob;
                    t.MaxFeePerBlobGas = 1;
                    t.BlobVersionedHashes = template.BlobVersionedHashes;
                    t.NetworkWrapper = template.NetworkWrapper;
                    t.Timestamp = (UInt256)i;
                })
                .WithMaxFeePerGas(1.GWei)
                .WithMaxPriorityFeePerGas(1.GWei)
                .SignedAndResolved(ecdsa, TestItem.PrivateKeys[i]).TestObject;
            _storage.Add(tx);
            _transactions[i] = tx;
        }

        if (Full() != TransactionCount || WithoutBlobs() != TransactionCount)
            throw new InvalidOperationException("A stored transaction was not found.");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _container.Dispose();
        _directory.Dispose();
    }

    [Benchmark(Baseline = true)]
    public int Full()
    {
        int found = 0;
        foreach (Transaction tx in _transactions)
        {
            if (_storage.TryGet(tx.Hash!.ValueHash256, tx.SenderAddress!, tx.Timestamp, out _)) found++;
        }

        return found;
    }

    [Benchmark]
    public int WithoutBlobs()
    {
        int found = 0;
        foreach (Transaction tx in _transactions)
        {
            if (_storage.TryGetWithoutBlobs(tx.Hash!.ValueHash256, tx.SenderAddress!, out _)) found++;
        }

        return found;
    }
}
