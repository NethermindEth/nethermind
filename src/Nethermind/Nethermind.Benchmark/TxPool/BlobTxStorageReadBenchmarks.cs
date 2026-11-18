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
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
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
    private MempoolTransaction[] _transactions = null!;
    private byte[][] _networkForms = null!;

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

        MempoolTransaction template = Build.A.Transaction.WithShardBlobTxTypeAndFields(BlobsPerTransaction, spec: Osaka.Instance).MempoolTestObject;
        EthereumEcdsa ecdsa = new(BlockchainIds.Mainnet);
        _transactions = new MempoolTransaction[TransactionCount];
        for (int i = 0; i < TransactionCount; i++)
        {
            MempoolTransaction tx = new(Build.A.Transaction
                .With(t =>
                {
                    t.Type = TxType.Blob;
                    t.MaxFeePerBlobGas = 1;
                    t.BlobVersionedHashes = template.BlobVersionedHashes;
                    t.Timestamp = (UInt256)i;
                })
                .WithMaxFeePerGas(1.GWei)
                .WithMaxPriorityFeePerGas(1.GWei)
                .SignedAndResolved(ecdsa, TestItem.PrivateKeys[i]).TestObject, template.NetworkWrapper);
            _storage.Add(tx);
            _transactions[i] = tx;
        }

        _networkForms = new byte[TransactionCount][];
        for (int i = 0; i < TransactionCount; i++)
        {
            _networkForms[i] = MempoolTxDecoder.Instance.Encode(_transactions[i]);
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
        foreach (MempoolTransaction tx in _transactions)
        {
            if (_storage.TryGet(tx.Hash!.ValueHash256, tx.SenderAddress!, tx.Timestamp, out _)) found++;
        }

        return found;
    }

    [Benchmark]
    public int WithoutBlobs()
    {
        int found = 0;
        foreach (MempoolTransaction tx in _transactions)
        {
            if (_storage.TryGetWithoutBlobs(tx.Hash!.ValueHash256, tx.SenderAddress!, out _)) found++;
        }

        return found;
    }

    /// <summary>The read of the blob pool's sidecar-free fallback, which a missing sidecar-free record takes.</summary>
    [Benchmark]
    public int FullRecordWithoutBlobs()
    {
        int found = 0;
        foreach (MempoolTransaction tx in _transactions)
        {
            if (((IBlobTxElidedReadStorage)_storage).TryGetFullRecordWithoutBlobs(tx.Hash!.ValueHash256, tx.SenderAddress!, tx.Timestamp, out MempoolTransaction read))
            {
                _ = BlobTransactionPayload.Elide(read);
                found++;
            }
        }

        return found;
    }

    /// <summary>The decode of blob transactions received in the network form.</summary>
    [Benchmark]
    public int DecodeNetworkForm()
    {
        int decoded = 0;
        foreach (byte[] networkForm in _networkForms)
        {
            RlpReader reader = new(networkForm);
            if (MempoolTxDecoder.Instance.Decode(ref reader) is not null) decoded++;
        }

        return decoded;
    }
}
