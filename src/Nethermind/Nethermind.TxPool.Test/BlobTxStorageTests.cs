// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
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

    private static IEnumerable<TestCaseData> CorruptLightTxRecords()
    {
        byte[] valid = LightTxDecoder.Encode(CreateBlobTransaction());

        yield return new TestCaseData(Array.Empty<byte>(), typeof(IndexOutOfRangeException)).SetName("empty_record");
        yield return new TestCaseData(valid[..2], typeof(ArgumentOutOfRangeException)).SetName("truncated_record");
        yield return new TestCaseData((byte[])[.. valid, 0x01], typeof(RlpException)).SetName("excess_trailing_field");
    }

    [TestCaseSource(nameof(CorruptLightTxRecords))]
    public void GetAll_should_skip_corrupt_light_tx_record(byte[] corruptRecord, Type expectedException)
    {
        MemColumnsDb<BlobTxsColumns> columnsDb = new();
        BlobTxStorage blobTxStorage = new(columnsDb);
        Transaction tx = CreateBlobTransaction();
        blobTxStorage.Add(tx);
        columnsDb.GetColumnDb(BlobTxsColumns.LightBlobTxs).Set(TestItem.KeccakA.Bytes, corruptRecord);

        List<LightTransaction> restored = [.. blobTxStorage.GetAll()];

        using (Assert.EnterMultipleScope())
        {
            // Pinned because the skip has to absorb all three roots: truncation surfaces as
            // ArgumentOutOfRangeException/IndexOutOfRangeException, not as RlpException.
            Assert.That(() => LightTxDecoder.Decode(corruptRecord), Throws.InstanceOf(expectedException));
            Assert.That(restored, Has.Count.EqualTo(1));
            Assert.That(restored[0].Hash, Is.EqualTo(tx.Hash));
        }
    }

    [Test]
    public void GetAll_should_warn_once_for_all_skipped_records()
    {
        TestLogger logger = new() { IsDebug = false, IsTrace = false };
        MemColumnsDb<BlobTxsColumns> columnsDb = new();
        BlobTxStorage blobTxStorage = new(columnsDb, new OneLoggerLogManager(new(logger)));
        blobTxStorage.Add(CreateBlobTransaction());
        IDb lightBlobTxsDb = columnsDb.GetColumnDb(BlobTxsColumns.LightBlobTxs);
        lightBlobTxsDb.Set(TestItem.KeccakA.Bytes, []);
        lightBlobTxsDb.Set(TestItem.KeccakB.Bytes, []);
        lightBlobTxsDb.Set(TestItem.KeccakC.Bytes, []);

        List<LightTransaction> restored = [.. blobTxStorage.GetAll()];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(restored, Has.Count.EqualTo(1));
            Assert.That(logger.LogList, Has.Count.EqualTo(1));
            Assert.That(logger.LogList[0], Does.Contain("3"));
        }
    }

    private static Transaction CreateBlobTransaction() => Build.A.Transaction
        .WithShardBlobTxTypeAndFields()
        .WithMaxFeePerGas(1.GWei)
        .WithMaxPriorityFeePerGas(1.GWei)
        .SignedAndResolved(new EthereumEcdsa(BlockchainIds.Mainnet), TestItem.PrivateKeyA).TestObject;

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
