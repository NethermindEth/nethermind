// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using Autofac;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using Nethermind.TxPool;
using Nethermind.TxPool.Collections;
using Testably.Abstractions;

namespace Nethermind.Benchmarks.TxPool;

/// <summary>
/// Answering one <c>PooledTransactions</c> request for blob transactions that only the persistent blob pool holds:
/// reading them from storage and writing the response items.
/// </summary>
[MemoryDiagnoser]
public class BlobTxServingBenchmarks
{
    private const int TransactionCount = 16;

    private TempPath _directory = null!;
    private IColumnsDb<BlobTxsColumns> _db = null!;
    private PersistentBlobTxDistinctSortedPool _pool = null!;
    private ValueHash256[] _hashes = null!;

    [Params(1, 6)]
    public int BlobsPerTransaction { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _directory = TempPath.GetTempDirectory();
        using (IContainer configs = new ContainerBuilder().AddModule(new TestNethermindModule()).Build())
        {
            _db = new RocksDbFactory(configs.Resolve<IRocksDbConfigFactory>(), configs.Resolve<IDbConfig>(),
                    new HyperClockCacheWrapper(), new RealFileSystem(), LimboLogs.Instance, _directory.Path)
                .CreateColumnsDb<BlobTxsColumns>(new DbSettings(DbNames.BlobTransactions, "blobs"));
        }

        // A cache of one keeps only the transaction inserted last, which is not requested, so every read misses it.
        TxPoolConfig config = new() { BlobCacheSize = 1, PersistentBlobStorageSize = TransactionCount + 1 };
        _pool = new PersistentBlobTxDistinctSortedPool(new BlobTxStorage(_db), config,
            Comparer<Transaction>.Create(static (x, y) => x.Hash!.CompareTo(y.Hash!)), LimboLogs.Instance);

        Transaction template = Build.A.Transaction.WithShardBlobTxTypeAndFields(BlobsPerTransaction, spec: Osaka.Instance).TestObject;
        EthereumEcdsa ecdsa = new(BlockchainIds.Mainnet);
        _hashes = new ValueHash256[TransactionCount];
        ValueHash256 cachedHash = default;
        for (int i = 0; i <= TransactionCount; i++)
        {
            Transaction tx = Build.A.Transaction
                .With(t =>
                {
                    t.Type = TxType.Blob;
                    t.MaxFeePerBlobGas = 1;
                    t.BlobVersionedHashes = template.BlobVersionedHashes;
                    t.NetworkWrapper = template.NetworkWrapper;
                })
                .WithMaxFeePerGas(1.GWei)
                .WithMaxPriorityFeePerGas(1.GWei)
                .SignedAndResolved(ecdsa, TestItem.PrivateKeys[i]).TestObject;
            if (!_pool.TryInsert(tx.Hash!, tx)) throw new InvalidOperationException("Transaction not inserted.");
            if (i < TransactionCount) _hashes[i] = tx.Hash!;
            else cachedHash = tx.Hash!;
        }

        if (Encoded() != Decoded()) throw new InvalidOperationException("The encoded response differs in length.");
        _pool.TryGetValue(cachedHash, out _);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _pool.Dispose();
        _db.Dispose();
        _directory.Dispose();
    }

    /// <summary>Decodes each stored transaction and encodes it again, as serving does before this change.</summary>
    [Benchmark(Baseline = true)]
    public int Decoded()
    {
        Transaction[] transactions = new Transaction[TransactionCount];
        int contentLength = 0;
        for (int i = 0; i < transactions.Length; i++)
        {
            if (!_pool.TryGetValue(_hashes[i], out transactions[i])) throw new InvalidOperationException("Transaction not found.");
            _ = transactions[i].GetLength();
            contentLength += TxDecoder.Instance.GetLength(transactions[i], RlpBehaviors.InMempoolForm);
        }

        int length = global::Nethermind.Serialization.Rlp.Rlp.LengthOfSequence(contentLength);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
        RlpWriter writer = RlpWriter.ContentWriter(buffer.AsSpan(0, length));
        RlpSequence sequence = writer.StartSequence(contentLength);
        foreach (Transaction transaction in transactions) TxDecoder.Instance.Encode(ref writer, transaction, RlpBehaviors.InMempoolForm);
        writer.EndSequence(sequence);
        ArrayPool<byte>.Shared.Return(buffer);
        return length;
    }

    /// <summary>Copies each stored transaction into its network form and writes that.</summary>
    [Benchmark]
    public int Encoded()
    {
        IOwnedReadOnlyList<byte>[] encodings = new IOwnedReadOnlyList<byte>[TransactionCount];
        int contentLength = 0;
        for (int i = 0; i < encodings.Length; i++)
        {
            if (!_pool.TryGetNetworkFormFromStorage(_hashes[i], out encodings[i])) throw new InvalidOperationException("Transaction not found.");
            contentLength += TxDecoder.GetWrappedTxLength(TxType.Blob, encodings[i].Count);
        }

        int length = global::Nethermind.Serialization.Rlp.Rlp.LengthOfSequence(contentLength);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
        RlpWriter writer = RlpWriter.ContentWriter(buffer.AsSpan(0, length));
        RlpSequence sequence = writer.StartSequence(contentLength);
        foreach (IOwnedReadOnlyList<byte> encoding in encodings)
        {
            writer.StartByteArray(encoding.Count, false);
            writer.WriteEncoded(encoding.AsSpan());
            encoding.Dispose();
        }

        writer.EndSequence(sequence);
        ArrayPool<byte>.Shared.Return(buffer);
        return length;
    }
}
