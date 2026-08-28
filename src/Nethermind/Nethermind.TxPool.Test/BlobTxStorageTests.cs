// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.TxPool.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class BlobTxStorageTests
{
    [Test]
    public void should_throw_when_trying_to_add_null_tx()
    {
        BlobTxStorage blobTxStorage = new();

        Action act = () => blobTxStorage.Add(null);
        Assert.That(act, Throws.TypeOf<ArgumentNullException>());
    }

    [Test]
    public void should_throw_when_trying_to_add_tx_with_null_hash()
    {
        BlobTxStorage blobTxStorage = new();

        Transaction tx = Build.A.Transaction.TestObject;
        tx.Hash = null;

        Action act = () => blobTxStorage.Add(tx);
        Assert.That(act, Throws.TypeOf<ArgumentNullException>());
    }

    [Test]
    public void TryGetMany_should_return_zero_for_empty_batch()
    {
        BlobTxStorage blobTxStorage = new();
        Transaction[] results = Array.Empty<Transaction>();

        int found = blobTxStorage.TryGetMany([], 0, results);
        Assert.That(found, Is.EqualTo(0));
    }

    [Test]
    public void TryGetMany_should_batch_retrieve_stored_transactions()
    {
        BlobTxStorage blobTxStorage = new();
        EthereumEcdsa ecdsa = new(BlockchainIds.Mainnet);

        Transaction[] txs = new Transaction[3];
        TxLookupKey[] keys = new TxLookupKey[3];

        for (int i = 0; i < 3; i++)
        {
            txs[i] = Build.A.Transaction
                .WithShardBlobTxTypeAndFields()
                .WithMaxFeePerGas(1.GWei)
                .WithMaxPriorityFeePerGas(1.GWei)
                .WithNonce((ulong)i)
                .SignedAndResolved(ecdsa, TestItem.PrivateKeys[i]).TestObject;

            blobTxStorage.Add(txs[i]);
            keys[i] = new TxLookupKey(txs[i].Hash, txs[i].SenderAddress!, txs[i].Timestamp);
        }

        Transaction[] results = new Transaction[3];
        int found = blobTxStorage.TryGetMany(keys, 3, results);

        Assert.That(found, Is.EqualTo(3));
        for (int i = 0; i < 3; i++)
        {
            Assert.That(results[i], Is.EqualTo(txs[i]).UsingTransactionComparer(nameof(Transaction.GasBottleneck), nameof(Transaction.PoolIndex)));
        }
    }

    [Test]
    public void TryGetMany_should_handle_mix_of_existing_and_missing_keys()
    {
        BlobTxStorage blobTxStorage = new();
        EthereumEcdsa ecdsa = new(BlockchainIds.Mainnet);

        Transaction[] txs = new Transaction[2];
        for (int i = 0; i < 2; i++)
        {
            txs[i] = Build.A.Transaction
                .WithShardBlobTxTypeAndFields()
                .WithMaxFeePerGas(1.GWei)
                .WithMaxPriorityFeePerGas(1.GWei)
                .WithNonce((ulong)i)
                .SignedAndResolved(ecdsa, TestItem.PrivateKeys[i]).TestObject;

            blobTxStorage.Add(txs[i]);
        }

        TxLookupKey[] keys = new TxLookupKey[3];
        keys[0] = new TxLookupKey(txs[0].Hash, txs[0].SenderAddress!, txs[0].Timestamp);
        keys[1] = new TxLookupKey(txs[1].Hash, txs[1].SenderAddress!, txs[1].Timestamp);
        keys[2] = new TxLookupKey(TestItem.KeccakA, TestItem.AddressC, UInt256.One);

        Transaction[] results = new Transaction[3];
        int found = blobTxStorage.TryGetMany(keys, 3, results);

        Assert.That(found, Is.EqualTo(2));
        Assert.That(results[0], Is.Not.Null);
        Assert.That(results[1], Is.Not.Null);
        Assert.That(results[2], Is.Null);
    }

    [Test]
    public void TryGetWithoutBlobs_should_return_tx_with_elided_blob_payloads()
    {
        BlobTxStorage blobTxStorage = new();
        Transaction tx = CreateBlobTransaction();

        blobTxStorage.Add(tx);

        Assert.That(blobTxStorage.TryGetWithoutBlobs(tx.Hash, tx.SenderAddress!, out Transaction elidedTx), Is.True);

        ShardBlobNetworkWrapper originalWrapper = (ShardBlobNetworkWrapper)tx.NetworkWrapper!;
        ShardBlobNetworkWrapper elidedWrapper = (ShardBlobNetworkWrapper)elidedTx.NetworkWrapper!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(elidedTx.Hash, Is.EqualTo(tx.Hash));
            Assert.That(elidedTx.Nonce, Is.EqualTo(tx.Nonce));
            Assert.That(elidedTx.SenderAddress, Is.EqualTo(tx.SenderAddress));
            Assert.That(elidedWrapper.Blobs, Is.Empty);
            Assert.That(elidedWrapper.Commitments, Is.EqualTo(originalWrapper.Commitments));
            Assert.That(elidedWrapper.Proofs, Is.EqualTo(originalWrapper.Proofs));
            Assert.That(elidedWrapper.Version, Is.EqualTo(originalWrapper.Version));
            Assert.That(elidedWrapper.CellMask, Is.EqualTo(BlobCellMask.Empty));
            Assert.That(elidedWrapper.Cells, Is.Null);
        }
    }

    [Test]
    public void TryGetWithoutBlobs_should_return_false_for_missing_tx()
    {
        BlobTxStorage blobTxStorage = new();

        Assert.That(blobTxStorage.TryGetWithoutBlobs(TestItem.KeccakA, TestItem.AddressA, out Transaction tx), Is.False);
        Assert.That(tx, Is.Null);
    }

    [Test]
    public void Add_should_not_rewrite_existing_elided_payload()
    {
        MemColumnsDb<BlobTxsColumns> columnsDb = new();
        MemDb fullBlobTxsDb = (MemDb)columnsDb.GetColumnDb(BlobTxsColumns.FullBlobTxs);
        BlobTxStorage blobTxStorage = new(columnsDb);
        Transaction tx = CreateBlobTransaction();

        blobTxStorage.Add(tx);
        long writesAfterInsert = fullBlobTxsDb.WritesCount;
        blobTxStorage.Add(tx);

        Assert.That(fullBlobTxsDb.WritesCount, Is.EqualTo(writesAfterInsert + 1));
    }

    [Test]
    public void AddWithoutBlobs_should_not_restore_deleted_transaction()
    {
        BlobTxStorage blobTxStorage = new();
        Transaction tx = CreateBlobTransaction();
        blobTxStorage.Add(tx);
        Assert.That(blobTxStorage.TryGet(tx.Hash, tx.SenderAddress!, tx.Timestamp, out Transaction storedTx), Is.True);

        blobTxStorage.Delete(tx.Hash, tx.Timestamp);
        blobTxStorage.AddWithoutBlobs(storedTx);

        Assert.That(blobTxStorage.TryGetWithoutBlobs(tx.Hash, tx.SenderAddress!, out _), Is.False);
    }

    [Test]
    public void Delete_should_remove_elided_payload_as_well()
    {
        TrackingColumnsDb columnsDb = new();
        BlobTxStorage blobTxStorage = new(columnsDb);
        Transaction tx = CreateBlobTransaction();

        blobTxStorage.Add(tx);
        blobTxStorage.Delete(tx.Hash, tx.Timestamp);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(blobTxStorage.TryGetWithoutBlobs(tx.Hash, tx.SenderAddress!, out _), Is.False);
            Assert.That(columnsDb.StartedWriteBatchCount, Is.EqualTo(1));
        }
    }

    [Test]
    public void DeleteMany_should_use_one_write_batch()
    {
        TrackingColumnsDb columnsDb = new();
        BlobTxStorage blobTxStorage = new(columnsDb);
        Transaction first = CreateBlobTransaction();
        Transaction second = Build.A.Transaction
            .WithShardBlobTxTypeAndFields()
            .WithNonce(1)
            .WithMaxFeePerGas(1.GWei)
            .WithMaxPriorityFeePerGas(1.GWei)
            .SignedAndResolved(new EthereumEcdsa(BlockchainIds.Mainnet), TestItem.PrivateKeyB).TestObject;
        blobTxStorage.Add(first);
        blobTxStorage.Add(second);
        TxLookupKey[] keys =
        [
            new(first.Hash, first.SenderAddress!, first.Timestamp),
            new(second.Hash, second.SenderAddress!, second.Timestamp)
        ];

        ((IBatchDeleteTxStorage)blobTxStorage).DeleteMany(keys);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(columnsDb.StartedWriteBatchCount, Is.EqualTo(1));
            Assert.That(blobTxStorage.TryGet(first.Hash, first.SenderAddress!, first.Timestamp, out _), Is.False);
            Assert.That(blobTxStorage.TryGet(second.Hash, second.SenderAddress!, second.Timestamp, out _), Is.False);
            Assert.That(blobTxStorage.TryGetWithoutBlobs(first.Hash, first.SenderAddress!, out _), Is.False);
            Assert.That(blobTxStorage.TryGetWithoutBlobs(second.Hash, second.SenderAddress!, out _), Is.False);
            Assert.That(blobTxStorage.GetAll(), Is.Empty);
        }
    }

    [Test]
    public void TryGetMany_should_handle_all_missing_keys()
    {
        BlobTxStorage blobTxStorage = new();

        TxLookupKey[] keys =
        [
            new TxLookupKey(TestItem.KeccakA, TestItem.AddressA, UInt256.One),
            new TxLookupKey(TestItem.KeccakB, TestItem.AddressB, UInt256.One),
        ];

        Transaction[] results = new Transaction[2];
        int found = blobTxStorage.TryGetMany(keys, 2, results);

        Assert.That(found, Is.EqualTo(0));
        Assert.That(results[0], Is.Null);
        Assert.That(results[1], Is.Null);
    }

    /// <remarks>
    /// A corrupt light record surfaces as one of three unrelated exception roots, so each case pins the one it
    /// exercises: <see cref="RlpReader"/> slices without bounds checks, which turns truncation into
    /// <see cref="ArgumentOutOfRangeException"/> or <see cref="IndexOutOfRangeException"/> rather than <see cref="RlpException"/>.
    /// </remarks>
    private static IEnumerable<TestCaseData> CorruptLightTxRecords()
    {
        yield return new TestCaseData((Func<byte[], byte[]>)(valid => valid[..2]), typeof(ArgumentOutOfRangeException))
            .SetName("GetAll_skips_unreadable_record(truncated)");
        yield return new TestCaseData((Func<byte[], byte[]>)(_ => []), typeof(IndexOutOfRangeException))
            .SetName("GetAll_skips_unreadable_record(empty)");
        yield return new TestCaseData((Func<byte[], byte[]>)(valid => [0x82, 0x00, 0x01, .. valid[1..]]), typeof(RlpException))
            .SetName("GetAll_skips_unreadable_record(non_canonical_scalar)");
        yield return new TestCaseData((Func<byte[], byte[]>)(_ => [0xff, 0xff, 0xff, 0xff]), typeof(RlpException))
            .SetName("GetAll_skips_unreadable_record(garbage)");
        // A record written by a newer version carrying a fifth optional field: every record fails after a downgrade.
        yield return new TestCaseData((Func<byte[], byte[]>)(valid => [.. valid, 0x01]), typeof(RlpException))
            .SetName("GetAll_skips_unreadable_record(extra_optional_field)");
    }

    [TestCaseSource(nameof(CorruptLightTxRecords))]
    public void GetAll_should_skip_unreadable_records_and_warn_once(Func<byte[], byte[]> corrupt, Type expectedDecodeException)
    {
        InterfaceLogger iLogger = Substitute.For<InterfaceLogger>();
        iLogger.IsWarn.Returns(true);

        MemColumnsDb<BlobTxsColumns> columnsDb = new();
        BlobTxStorage blobTxStorage = new(columnsDb, new OneLoggerLogManager(new ILogger(iLogger)));
        Transaction[] txs = [CreateBlobTransaction(TestItem.PrivateKeyA), CreateBlobTransaction(TestItem.PrivateKeyB)];

        byte[] corruptRecord = corrupt(LightTxDecoder.Encode(txs[0]));
        Exception decodeFailure = Assert.Throws(Is.InstanceOf(expectedDecodeException), () => LightTxDecoder.Decode(corruptRecord),
            "case no longer exercises the decode failure mode it is meant to cover")!;

        blobTxStorage.Add(txs[0]);
        columnsDb.GetColumnDb(BlobTxsColumns.LightBlobTxs).Set(TestItem.KeccakA, corruptRecord);
        blobTxStorage.Add(txs[1]);

        LightTransaction[] restored = blobTxStorage.GetAll().ToArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(restored.Select(static tx => tx.Hash), Is.EquivalentTo(txs.Select(static tx => tx.Hash)));
            iLogger.Received(1).Warn(Arg.Is<string>(message =>
                message.Contains("Skipped 1 of 3 ") && message.Contains($"{decodeFailure.GetType().Name}: {decodeFailure.Message}")));
        }
    }

    private static Transaction CreateBlobTransaction() => CreateBlobTransaction(TestItem.PrivateKeyA);

    private static Transaction CreateBlobTransaction(PrivateKey signer) => Build.A.Transaction
        .WithShardBlobTxTypeAndFields()
        .WithMaxFeePerGas(1.GWei)
        .WithMaxPriorityFeePerGas(1.GWei)
        .SignedAndResolved(new EthereumEcdsa(BlockchainIds.Mainnet), signer).TestObject;

    private sealed class TrackingColumnsDb : IColumnsDb<BlobTxsColumns>
    {
        private readonly MemColumnsDb<BlobTxsColumns> _inner = new();

        public int StartedWriteBatchCount { get; private set; }
        public IEnumerable<BlobTxsColumns> ColumnKeys => _inner.ColumnKeys;

        public IDb GetColumnDb(BlobTxsColumns key) => _inner.GetColumnDb(key);

        public IColumnsWriteBatch<BlobTxsColumns> StartWriteBatch()
        {
            StartedWriteBatchCount++;
            return _inner.StartWriteBatch();
        }

        public IColumnDbSnapshot<BlobTxsColumns> CreateSnapshot() => _inner.CreateSnapshot();

        public void Flush(bool onlyWal = false) => _inner.Flush(onlyWal);

        public void Dispose() => _inner.Dispose();
    }
}
