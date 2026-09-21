// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Collections;
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
    public void TryGetBlobTransactionsFromBlock_should_decode_legacy_entries([Range(1, 3)] int txCount)
    {
        const ulong blockNumber = 42;
        MemColumnsDb<BlobTxsColumns> columnsDb = new();
        BlobTxStorage blobTxStorage = new(columnsDb);
        Transaction[] transactions = CreateBlobTransactions(txCount);

        byte[] legacyEntry = TxDecoder.Instance.Encode(transactions, RlpBehaviors.InMempoolForm).Bytes;
        columnsDb.GetColumnDb(BlobTxsColumns.ProcessedTxs)
            .Set(blockNumber.ToBigEndianSpanWithoutLeadingZeros(out _), legacyEntry);

        Assert.That(blobTxStorage.TryGetBlobTransactionsFromBlock(blockNumber, out Transaction[] decoded), Is.True);
        AssertNetworkWrappersEqual(transactions, decoded!);
    }

    [Test]
    public void TryGetBlobTransactionsFromBlock_should_roundtrip_current_v1_entries([Range(1, 2)] int txCount)
    {
        const ulong blockNumber = 42;
        BlobTxStorage blobTxStorage = new();
        Transaction[] transactions = CreateBlobTransactions(txCount);
        BlobCellMask cellMask = BlobCellMask.FromIndices([1, 3]);

        for (int i = 0; i < transactions.Length; i++)
        {
            ShardBlobNetworkWrapper wrapper = (ShardBlobNetworkWrapper)transactions[i].NetworkWrapper!;
            transactions[i].NetworkWrapper = wrapper with
            {
                Blobs = [],
                Version = ProofVersion.V1,
                CellMask = cellMask,
                Cells = [[(byte)(i + 1)], [(byte)(i + 2)]]
            };
            transactions[i].ClearLengthCache();
        }

        using (ArrayPoolListRef<Transaction> pooledTransactions = new(transactions.AsSpan()))
        {
            blobTxStorage.AddBlobTransactionsFromBlock(blockNumber, pooledTransactions);
        }

        Assert.That(blobTxStorage.TryGetBlobTransactionsFromBlock(blockNumber, out Transaction[] decoded), Is.True);
        AssertNetworkWrappersEqual(transactions, decoded!);
    }

    [Test]
    public void Missing_processed_payload_is_a_cache_miss([Values(0, 1)] int missingIndex)
    {
        using MemColumnsDb<BlobTxsColumns> db = new();
        BlobTxStorage storage = new(db);
        using ArrayPoolListRef<Transaction> transactions = new(2);
        transactions.Add(CreateBlobTransaction(TestItem.PrivateKeyA, 0));
        transactions.Add(CreateBlobTransaction(TestItem.PrivateKeyA, 1));
        storage.AddBlobTransactionsFromBlock(358, transactions);
        IDb processed = db.GetColumnDb(BlobTxsColumns.ProcessedTxs);
        byte[][] payloadKeys = processed.GetAllKeys().Where(static key => key.Length == 12).OrderBy(static key => key[^1]).ToArray();
        processed.Remove(payloadKeys[missingIndex]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(storage.TryGetBlobTransactionsFromBlock(358, out Transaction[] restored), Is.False);
            Assert.That(restored, Is.Null);
        }
    }

    [Test]
    public void Invalid_processed_index_is_a_cache_miss_and_can_be_replaced_or_deleted(
        [Values("", "00", "000000", "000000000001", "0000000000", "00ffffffff", "007fffffff", "0000014587")] string encodedIndex,
        [Values] bool replace, [Values] bool orphanedPayload)
    {
        using MemColumnsDb<BlobTxsColumns> db = new();
        BlobTxStorage storage = new(db);
        IDb processed = db.GetColumnDb(BlobTxsColumns.ProcessedTxs);
        byte[] orphanKey = new byte[sizeof(ulong) + sizeof(int)];
        BinaryPrimitives.WriteUInt64BigEndian(orphanKey, 358);
        BinaryPrimitives.WriteInt32BigEndian(orphanKey.AsSpan(sizeof(ulong)), 3);
        if (orphanedPayload) processed.PutSpan(orphanKey, [1]);
        processed.PutSpan(358UL.ToBigEndianSpanWithoutLeadingZeros(out _), Convert.FromHexString(encodedIndex));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(storage.TryGetBlobTransactionsFromBlock(358, out Transaction[] restored), Is.False);
            Assert.That(restored, Is.Null);
        }
        if (replace)
        {
            using ArrayPoolListRef<Transaction> transactions = new(1, CreateBlobTransaction());
            storage.AddBlobTransactionsFromBlock(358, transactions);
            Assert.That(storage.TryGetBlobTransactionsFromBlock(358, out Transaction[] restored), Is.True);
            AssertProcessedTransactions(transactions, restored);
        }

        storage.DeleteBlobTransactionsFromBlock(358);
        // Payloads orphaned by a damaged index are reclaimed by finalization cleanup.
        Assert.That(processed.GetAllKeys(), Is.EquivalentTo(orphanedPayload ? new[] { orphanKey } : Array.Empty<byte[]>()));
    }

    [Test]
    public void Processed_transactions_survive_pool_deletion_and_storage_restart(
        [Values(1, 4, 16)] int count, [Values] bool stored)
    {
        using MemColumnsDb<BlobTxsColumns> db = new();
        BlobTxStorage storage = new(db);
        using ArrayPoolListRef<Transaction> transactions = new(count);
        for (int i = 0; i < count; i++)
        {
            Transaction tx = CreateBlobTransaction(TestItem.PrivateKeyA, i, blobCount: i % 4 == 1 ? 3 : 1);
            transactions.Add(tx);
            if (stored) storage.Add(tx);
        }

        storage.AddBlobTransactionsFromBlock(358, transactions);
        foreach (Transaction tx in transactions) storage.Delete(tx.Hash, tx.Timestamp);
        storage = new BlobTxStorage(db);

        Assert.That(storage.TryGetBlobTransactionsFromBlock(358, out Transaction[] restored), Is.True);
        AssertProcessedTransactions(transactions, restored);
        storage.DeleteBlobTransactionsFromBlock(358);
        Assert.That(db.GetColumnDb(BlobTxsColumns.ProcessedTxs).GetAllKeys(), Is.Empty);
    }

    [Test]
    public void Processed_transactions_preserve_pending_sidecar_changes()
    {
        using MemColumnsDb<BlobTxsColumns> db = new();
        BlobTxStorage storage = new(db);
        Transaction tx = CreateBlobTransaction();
        storage.Add(tx);
        ShardBlobNetworkWrapper wrapper = (ShardBlobNetworkWrapper)tx.NetworkWrapper;
        wrapper.Proofs[0][0] ^= 1;
        using ArrayPoolListRef<Transaction> transactions = new(1, tx);

        storage.AddBlobTransactionsFromBlock(358, transactions);

        Assert.That(storage.TryGetBlobTransactionsFromBlock(358, out Transaction[] restored), Is.True);
        AssertProcessedTransactions(transactions, restored);
    }

    [Test]
    public void Processed_transactions_read_replace_and_delete_legacy_records([Values] bool replace)
    {
        using MemColumnsDb<BlobTxsColumns> db = new();
        BlobTxStorage storage = new(db);
        using ArrayPoolListRef<Transaction> transactions = new(1, CreateBlobTransaction());
        using ArrayPoolSpan<byte> legacy = TxDecoder.Instance.EncodeToArrayPoolSpan(transactions.AsSpan(), RlpBehaviors.InMempoolForm | RlpBehaviors.Storage);
        db.GetColumnDb(BlobTxsColumns.ProcessedTxs).PutSpan(358UL.ToBigEndianSpanWithoutLeadingZeros(out _), legacy);

        if (replace) storage.AddBlobTransactionsFromBlock(358, transactions);

        Assert.That(storage.TryGetBlobTransactionsFromBlock(358, out Transaction[] restored), Is.True);
        AssertProcessedTransactions(transactions, restored);
        storage.DeleteBlobTransactionsFromBlock(358);
        Assert.That(db.GetColumnDb(BlobTxsColumns.ProcessedTxs).GetAllKeys(), Is.Empty);
    }

    [Test]
    public void Replacing_processed_block_removes_obsolete_payloads_and_preserves_other_blocks()
    {
        using MemColumnsDb<BlobTxsColumns> db = new();
        BlobTxStorage storage = new(db);
        using ArrayPoolListRef<Transaction> original = new(2, CreateBlobTransaction(), CreateBlobTransaction(TestItem.PrivateKeyB));
        using ArrayPoolListRef<Transaction> replacement = new(1, CreateBlobTransaction(TestItem.PrivateKeyC));
        storage.AddBlobTransactionsFromBlock(358, original);
        storage.AddBlobTransactionsFromBlock(359, original);

        storage.AddBlobTransactionsFromBlock(358, replacement);

        Assert.That(storage.TryGetBlobTransactionsFromBlock(358, out Transaction[] restored), Is.True);
        AssertProcessedTransactions(replacement, restored);
        Assert.That(db.GetColumnDb(BlobTxsColumns.ProcessedTxs).GetAllKeys().Count(), Is.EqualTo(5));
        storage.DeleteBlobTransactionsFromBlock(358);
        Assert.That(storage.TryGetBlobTransactionsFromBlock(359, out restored), Is.True);
        AssertProcessedTransactions(original, restored);
        storage.DeleteBlobTransactionsFromBlock(359);
        Assert.That(db.GetColumnDb(BlobTxsColumns.ProcessedTxs).GetAllKeys(), Is.Empty);
    }

    [Test]
    public void Processed_block_write_failure_preserves_previous_block([Values] bool stored)
    {
        using TrackingColumnsDb db = new();
        BlobTxStorage storage = new(db);
        using ArrayPoolListRef<Transaction> original = new(1, CreateBlobTransaction());
        using ArrayPoolListRef<Transaction> replacement = new(2, CreateBlobTransaction(TestItem.PrivateKeyB), CreateBlobTransaction(TestItem.PrivateKeyC));
        if (stored)
        {
            foreach (Transaction tx in replacement) storage.Add(tx);
        }
        storage.AddBlobTransactionsFromBlock(358, original);
        db.FailProcessedWriteAfter = 1;
        Transaction[] replacementTransactions = replacement.AsSpan().ToArray();

        Assert.Throws<InvalidOperationException>(() => SaveProcessed(storage, replacementTransactions));

        Assert.That(storage.TryGetBlobTransactionsFromBlock(358, out Transaction[] restored), Is.True);
        AssertProcessedTransactions(original, restored);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(db.GetColumnDb(BlobTxsColumns.ProcessedTxs).GetAllKeys().Count(), Is.EqualTo(2));
            Assert.That(db.ActiveSpans, Is.Zero);
        }

        static void SaveProcessed(BlobTxStorage storage, Transaction[] transactions)
        {
            using ArrayPoolListRef<Transaction> batch = new(transactions.Length);
            batch.AddRange(transactions);
            storage.AddBlobTransactionsFromBlock(358, batch);
        }
    }

    private static void AssertProcessedTransactions(in ArrayPoolListRef<Transaction> expected, Transaction[] actual)
    {
        Assert.That(actual.Length, Is.EqualTo(expected.Count));
        for (int i = 0; i < actual.Length; i++)
        {
            ShardBlobNetworkWrapper expectedWrapper = (ShardBlobNetworkWrapper)expected[i].NetworkWrapper;
            ShardBlobNetworkWrapper actualWrapper = (ShardBlobNetworkWrapper)actual[i].NetworkWrapper;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(actual[i], Is.EqualTo(expected[i]).UsingTransactionComparer(
                    nameof(Transaction.SenderAddress), nameof(Transaction.Timestamp),
                    nameof(Transaction.GasBottleneck), nameof(Transaction.PoolIndex)), $"Transaction {i}");
                Assert.That(actualWrapper.CellMask, Is.EqualTo(expectedWrapper.CellMask), $"Cell mask {i}");
                Assert.That(actualWrapper.Cells, Is.EqualTo(expectedWrapper.Cells), $"Cells {i}");
            }
        }
    }

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
        columnsDb.ResetWriteBatchTracking();
        blobTxStorage.Delete(tx.Hash, tx.Timestamp);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(blobTxStorage.TryGetWithoutBlobs(tx.Hash, tx.SenderAddress!, out _), Is.False);
            Assert.That(columnsDb.StartedWriteBatchCount, Is.EqualTo(1));
        }
    }

    [Test]
    public void Delete_should_not_commit_partial_batch_when_column_write_fails()
    {
        TrackingColumnsDb columnsDb = new();
        BlobTxStorage blobTxStorage = new(columnsDb);
        Transaction transaction = CreateBlobTransaction();
        blobTxStorage.Add(transaction);
        columnsDb.FailNextLightColumnWrite = true;

        Assert.That(
            () => blobTxStorage.Delete(transaction.Hash, transaction.Timestamp),
            Throws.TypeOf<InvalidOperationException>());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(blobTxStorage.TryGet(
                transaction.Hash,
                transaction.SenderAddress!,
                transaction.Timestamp,
                out _), Is.True);
            Assert.That(blobTxStorage.TryGetWithoutBlobs(
                transaction.Hash,
                transaction.SenderAddress!,
                out _), Is.True);
            Assert.That(CountLightTransactions(blobTxStorage), Is.EqualTo(1));
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
        BlobTxDeleteKey[] keys =
        [
            new(first.Hash, first.Timestamp),
            new(second.Hash, second.Timestamp)
        ];

        columnsDb.ResetWriteBatchTracking();
        ((IAtomicBlobTxStorage)blobTxStorage).DeleteMany(keys);

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
    public void Add_should_use_one_write_batch_across_columns()
    {
        TrackingColumnsDb columnsDb = new();
        BlobTxStorage blobTxStorage = new(columnsDb);
        Transaction transaction = CreateBlobTransaction();

        blobTxStorage.Add(transaction);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(columnsDb.StartedWriteBatchCount, Is.EqualTo(1));
            Assert.That(blobTxStorage.TryGet(
                transaction.Hash,
                transaction.SenderAddress!,
                transaction.Timestamp,
                out _), Is.True);
            Assert.That(blobTxStorage.TryGetWithoutBlobs(
                transaction.Hash,
                transaction.SenderAddress!,
                out _), Is.True);
            Assert.That(CountLightTransactions(blobTxStorage), Is.EqualTo(1));
        }
    }

    [Test]
    public void Replace_should_remove_obsolete_body_in_same_write_batch()
    {
        TrackingColumnsDb columnsDb = new();
        BlobTxStorage blobTxStorage = new(columnsDb);
        Transaction transaction = CreateBlobTransaction();
        blobTxStorage.Add(transaction);
        UInt256 obsoleteTimestamp = transaction.Timestamp;
        transaction.Timestamp += UInt256.One;
        columnsDb.ResetWriteBatchTracking();

        ((IAtomicBlobTxStorage)blobTxStorage).Replace(transaction, [obsoleteTimestamp]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(columnsDb.StartedWriteBatchCount, Is.EqualTo(1));
            Assert.That(blobTxStorage.TryGet(
                transaction.Hash,
                transaction.SenderAddress!,
                obsoleteTimestamp,
                out _), Is.False);
            Assert.That(blobTxStorage.TryGet(
                transaction.Hash,
                transaction.SenderAddress!,
                transaction.Timestamp,
                out _), Is.True);
            Assert.That(CountLightTransactions(blobTxStorage), Is.EqualTo(1));
        }
    }

    [Test]
    public void Replace_should_not_commit_partial_batch_when_column_write_fails()
    {
        TrackingColumnsDb columnsDb = new();
        BlobTxStorage blobTxStorage = new(columnsDb);
        Transaction transaction = CreateBlobTransaction();
        blobTxStorage.Add(transaction);
        UInt256 originalTimestamp = transaction.Timestamp;
        transaction.Timestamp += UInt256.One;
        columnsDb.FailNextLightColumnWrite = true;

        Assert.That(
            () => ((IAtomicBlobTxStorage)blobTxStorage).Replace(transaction, [originalTimestamp]),
            Throws.TypeOf<InvalidOperationException>());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(blobTxStorage.TryGet(
                transaction.Hash,
                transaction.SenderAddress!,
                originalTimestamp,
                out _), Is.True);
            Assert.That(blobTxStorage.TryGet(
                transaction.Hash,
                transaction.SenderAddress!,
                transaction.Timestamp,
                out _), Is.False);
            Assert.That(blobTxStorage.TryGetWithoutBlobs(
                transaction.Hash,
                transaction.SenderAddress!,
                out _), Is.True);
            Assert.That(CountLightTransactions(blobTxStorage), Is.EqualTo(1));
        }
    }

    [Test]
    public void DeleteMany_should_not_commit_partial_batch_when_column_write_fails()
    {
        TrackingColumnsDb columnsDb = new();
        BlobTxStorage blobTxStorage = new(columnsDb);
        Transaction transaction = CreateBlobTransaction();
        blobTxStorage.Add(transaction);
        columnsDb.FailNextLightColumnWrite = true;

        Assert.That(
            () => ((IAtomicBlobTxStorage)blobTxStorage).DeleteMany(
                [new BlobTxDeleteKey(transaction.Hash!.ValueHash256, transaction.Timestamp)]),
            Throws.TypeOf<InvalidOperationException>());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(blobTxStorage.TryGet(
                transaction.Hash,
                transaction.SenderAddress!,
                transaction.Timestamp,
                out _), Is.True);
            Assert.That(blobTxStorage.TryGetWithoutBlobs(
                transaction.Hash,
                transaction.SenderAddress!,
                out _), Is.True);
            Assert.That(CountLightTransactions(blobTxStorage), Is.EqualTo(1));
        }
    }

    private static int CountLightTransactions(BlobTxStorage storage)
    {
        int count = 0;
        foreach (LightTransaction _ in storage.GetAll())
        {
            count++;
        }

        return count;
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

    private static Transaction[] CreateBlobTransactions(int count)
    {
        Transaction[] transactions = new Transaction[count];
        for (int i = 0; i < count; i++)
        {
            transactions[i] = CreateBlobTransaction(TestItem.PrivateKeys[i], i);
        }

        return transactions;
    }

    private static void AssertNetworkWrappersEqual(Transaction[] expected, Transaction[] actual)
    {
        Assert.That(actual, Has.Length.EqualTo(expected.Length));
        for (int i = 0; i < expected.Length; i++)
        {
            ShardBlobNetworkWrapper expectedWrapper = (ShardBlobNetworkWrapper)expected[i].NetworkWrapper!;
            ShardBlobNetworkWrapper actualWrapper = (ShardBlobNetworkWrapper)actual[i].NetworkWrapper!;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(actualWrapper.Blobs, Is.EqualTo(expectedWrapper.Blobs));
                Assert.That(actualWrapper.Commitments, Is.EqualTo(expectedWrapper.Commitments));
                Assert.That(actualWrapper.Proofs, Is.EqualTo(expectedWrapper.Proofs));
                Assert.That(actualWrapper.Version, Is.EqualTo(expectedWrapper.Version));
                Assert.That(actualWrapper.CellMask, Is.EqualTo(expectedWrapper.CellMask));
                Assert.That(actualWrapper.Cells, Is.EqualTo(expectedWrapper.Cells));
            }
        }
    }

    private static Transaction CreateBlobTransaction(PrivateKey signer, int nonce = 0, int blobCount = 1) => Build.A.Transaction
        .WithShardBlobTxTypeAndFields(blobCount)
        .WithNonce(nonce)
        .WithMaxFeePerGas(1.GWei)
        .WithMaxPriorityFeePerGas(1.GWei)
        .SignedAndResolved(new EthereumEcdsa(BlockchainIds.Mainnet), signer).TestObject;

    private sealed class TrackingColumnsDb : IColumnsDb<BlobTxsColumns>
    {
        private readonly MemColumnsDb<BlobTxsColumns> _inner = new();
        private readonly Dictionary<BlobTxsColumns, IDb> _columnDbs = [];

        public int StartedWriteBatchCount { get; private set; }
        public bool FailNextLightColumnWrite { get; set; }
        public int? FailProcessedWriteAfter { get; set; }
        public int ActiveSpans => _columnDbs.Values.Cast<DirectWriteRejectingDb>().Sum(static db => db.ActiveSpans);
        public IEnumerable<BlobTxsColumns> ColumnKeys => _inner.ColumnKeys;

        public IDb GetColumnDb(BlobTxsColumns key)
        {
            if (!_columnDbs.TryGetValue(key, out IDb db))
            {
                db = new DirectWriteRejectingDb(_inner.GetColumnDb(key), key);
                _columnDbs.Add(key, db);
            }

            return db;
        }

        public IColumnsWriteBatch<BlobTxsColumns> StartWriteBatch()
        {
            StartedWriteBatchCount++;
            return new TrackingColumnsWriteBatch(_inner.StartWriteBatch(), this);
        }

        public void ResetWriteBatchTracking() => StartedWriteBatchCount = 0;

        public IColumnDbSnapshot<BlobTxsColumns> CreateSnapshot() => _inner.CreateSnapshot();

        public void Flush(bool onlyWal = false) => _inner.Flush(onlyWal);

        public void Dispose() => _inner.Dispose();
    }

    private sealed class TrackingColumnsWriteBatch(
        IColumnsWriteBatch<BlobTxsColumns> inner,
        TrackingColumnsDb owner) : IColumnsWriteBatch<BlobTxsColumns>
    {
        public IWriteBatch GetColumnBatch(BlobTxsColumns key)
        {
            IWriteBatch batch = inner.GetColumnBatch(key);
            if (key == BlobTxsColumns.LightBlobTxs && owner.FailNextLightColumnWrite)
            {
                owner.FailNextLightColumnWrite = false;
                return new FailingWriteBatch(batch);
            }
            if (key == BlobTxsColumns.ProcessedTxs && owner.FailProcessedWriteAfter is { } successfulWrites)
            {
                owner.FailProcessedWriteAfter = null;
                return new FailingWriteBatch(batch, successfulWrites);
            }

            return batch;
        }

        public void Clear() => inner.Clear();

        public void Dispose() => inner.Dispose();
    }

    private sealed class FailingWriteBatch(IWriteBatch inner, int successfulWrites = 0) : IWriteBatch
    {
        private int _writes;

        public void Set(ReadOnlySpan<byte> key, byte[] value, WriteFlags flags = WriteFlags.None)
        {
            if (_writes++ == successfulWrites) throw new InvalidOperationException("Simulated column write failure.");
            inner.Set(key, value, flags);
        }

        public void Merge(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None) =>
            inner.Merge(key, value, flags);

        public void Clear() => inner.Clear();

        public void Dispose() { }
    }

    private sealed class DirectWriteRejectingDb(IDb inner, BlobTxsColumns column) : IDb
    {
        public string Name => inner.Name;
        public int ActiveSpans { get; private set; }

        public KeyValuePair<byte[], byte[]>[] this[byte[][] keys] => inner[keys];

        public byte[] Get(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) => inner.Get(key, flags);

        public Span<byte> GetSpan(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
        {
            Span<byte> value = inner.GetSpan(key, flags);
            if (!value.IsEmpty) ActiveSpans++;
            return value;
        }

        public void DangerousReleaseMemory(in ReadOnlySpan<byte> value)
        {
            if (!value.IsEmpty) ActiveSpans--;
            inner.DangerousReleaseMemory(value);
        }

        public void Set(ReadOnlySpan<byte> key, byte[] value, WriteFlags flags = WriteFlags.None)
        {
            if (column == BlobTxsColumns.LightBlobTxs
                || column == BlobTxsColumns.FullBlobTxs && key.Length == 64)
            {
                throw new InvalidOperationException("Full and light blob transaction writes must use a columns batch.");
            }

            inner.Set(key, value, flags);
        }

        public IEnumerable<KeyValuePair<byte[], byte[]>> GetAll(bool ordered = false) => inner.GetAll(ordered);

        public IEnumerable<byte[]> GetAllKeys(bool ordered = false) => inner.GetAllKeys(ordered);

        public IEnumerable<byte[]> GetAllValues(bool ordered = false) => inner.GetAllValues(ordered);

        public IWriteBatch StartWriteBatch() =>
            throw new InvalidOperationException("Blob transaction writes must use a columns batch.");

        public void Flush(bool onlyWal = false) => inner.Flush(onlyWal);

        public void Dispose() { }
    }
}
