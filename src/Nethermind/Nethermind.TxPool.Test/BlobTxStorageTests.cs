// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using CkzgLib;
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

    // Persisted through the same InMempoolForm as a type-3, so the type and sidecar both have to survive.
    [Test]
    public void should_roundtrip_blob_carrying_frame_tx_with_sidecar()
    {
        const ProofVersion version = ProofVersion.V1;
        BlobTxStorage blobTxStorage = new();
        Transaction tx = BuildBlobCarryingFrameTx(blobCount: 2, version);
        blobTxStorage.Add(tx);

        Assert.That(blobTxStorage.TryGet(tx.Hash, tx.SenderAddress!, tx.Timestamp, out Transaction full), Is.True);
        ShardBlobNetworkWrapper expected = (ShardBlobNetworkWrapper)tx.NetworkWrapper!;
        ShardBlobNetworkWrapper actual = (ShardBlobNetworkWrapper)full!.NetworkWrapper!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(full.Type, Is.EqualTo(TxType.FrameTx));
            Assert.That(actual.Version, Is.EqualTo(version));
            Assert.That(actual.Blobs, Is.EqualTo(expected.Blobs));
            Assert.That(actual.Commitments, Is.EqualTo(expected.Commitments));
            Assert.That(actual.Proofs, Is.EqualTo(expected.Proofs));

            LightTransaction light = blobTxStorage.GetAll().Single();
            Assert.That(light.Type, Is.EqualTo(TxType.FrameTx));
            Assert.That(light.GetProofVersion(), Is.EqualTo(version));
            Assert.That(light.BlobVersionedHashes, Is.EqualTo(tx.BlobVersionedHashes));
        }
    }

    private static Transaction BuildBlobCarryingFrameTx(int blobCount, ProofVersion version)
    {
        // The count only has to satisfy the RLP round-trip; nothing here verifies KZG.
        int proofsCount = version is ProofVersion.V1 ? blobCount * Ckzg.CellsPerExtBlob : blobCount;
        byte[][] versionedHashes = new byte[blobCount][];
        byte[][] blobs = new byte[blobCount][];
        byte[][] commitments = new byte[blobCount][];
        byte[][] proofs = new byte[proofsCount][];
        for (int i = 0; i < blobCount; i++)
        {
            byte[] hash = new byte[Eip4844Constants.BytesPerBlobVersionedHash];
            hash[0] = KzgPolynomialCommitments.KzgBlobHashVersionV1;
            hash[1] = (byte)i;
            versionedHashes[i] = hash;
            blobs[i] = [(byte)i];
            commitments[i] = [(byte)(i + 1)];
        }
        for (int i = 0; i < proofsCount; i++)
        {
            proofs[i] = [(byte)(i + 2)];
        }

        Transaction tx = new()
        {
            Type = TxType.FrameTx,
            ChainId = TestBlockchainIds.ChainId,
            Nonce = 0,
            SenderAddress = TestItem.AddressA,
            GasLimit = 1_000_000,
            GasPrice = 1,
            DecodedMaxFeePerGas = 100,
            MaxFeePerBlobGas = 1,
            Frames =
            [
                new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit: 100_000, UInt256.Zero, default),
            ],
            FrameSignatures = [],
            BlobVersionedHashes = versionedHashes,
            NetworkWrapper = new ShardBlobNetworkWrapper(blobs, commitments, proofs, version),
        };
        tx.Hash = tx.CalculateHash();
        return tx;
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

    private static Transaction CreateBlobTransaction(PrivateKey signer) => Build.A.Transaction
        .WithShardBlobTxTypeAndFields()
        .WithMaxFeePerGas(1.GWei)
        .WithMaxPriorityFeePerGas(1.GWei)
        .SignedAndResolved(new EthereumEcdsa(BlockchainIds.Mainnet), signer).TestObject;

    private sealed class TrackingColumnsDb : IColumnsDb<BlobTxsColumns>
    {
        private readonly MemColumnsDb<BlobTxsColumns> _inner = new();
        private readonly Dictionary<BlobTxsColumns, IDb> _columnDbs = [];

        public int StartedWriteBatchCount { get; private set; }
        public bool FailNextLightColumnWrite { get; set; }
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

            return batch;
        }

        public void Clear() => inner.Clear();

        public void Dispose() => inner.Dispose();
    }

    private sealed class FailingWriteBatch(IWriteBatch inner) : IWriteBatch
    {
        public void Set(ReadOnlySpan<byte> key, byte[] value, WriteFlags flags = WriteFlags.None) =>
            throw new InvalidOperationException("Simulated column write failure.");

        public void Merge(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None) =>
            inner.Merge(key, value, flags);

        public void Clear() => inner.Clear();

        public void Dispose() { }
    }

    private sealed class DirectWriteRejectingDb(IDb inner, BlobTxsColumns column) : IDb
    {
        public string Name => inner.Name;

        public KeyValuePair<byte[], byte[]>[] this[byte[][] keys] => inner[keys];

        public byte[] Get(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) => inner.Get(key, flags);

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
