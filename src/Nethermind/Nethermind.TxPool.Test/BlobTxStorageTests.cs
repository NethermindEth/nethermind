// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
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
        EthereumEcdsa ecdsa = new(BlockchainIds.Mainnet);

        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields()
            .WithMaxFeePerGas(1.GWei)
            .WithMaxPriorityFeePerGas(1.GWei)
            .SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;

        blobTxStorage.Add(tx);

        Assert.That(blobTxStorage.TryGetWithoutBlobs(tx.Hash, tx.SenderAddress!, tx.Timestamp, out Transaction elidedTx), Is.True);

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
        }
    }

    [Test]
    public void TryGetWithoutBlobs_should_return_false_for_missing_tx()
    {
        BlobTxStorage blobTxStorage = new();

        Assert.That(blobTxStorage.TryGetWithoutBlobs(TestItem.KeccakA, TestItem.AddressA, UInt256.One, out Transaction tx), Is.False);
        Assert.That(tx, Is.Null);
    }

    [Test]
    public void TryGetWithoutBlobs_should_upgrade_legacy_record_without_elided_payload()
    {
        MemColumnsDb<BlobTxsColumns> columnsDb = new();
        IDb fullBlobTxsDb = columnsDb.GetColumnDb(BlobTxsColumns.FullBlobTxs);
        IDb lightBlobTxsDb = columnsDb.GetColumnDb(BlobTxsColumns.LightBlobTxs);
        BlobTxStorage blobTxStorage = new(columnsDb);
        EthereumEcdsa ecdsa = new(BlockchainIds.Mainnet);

        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields()
            .WithMaxFeePerGas(1.GWei)
            .WithMaxPriorityFeePerGas(1.GWei)
            .SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;

        // simulate a record persisted before the sidecar-free record existed
        byte[] fullKey = new byte[64];
        tx.Timestamp.ToBigEndian(fullKey);
        tx.Hash.BytesToArray().CopyTo(fullKey, 32);
        fullBlobTxsDb.Set(fullKey, Rlp.Encode(tx, RlpBehaviors.InMempoolForm | RlpBehaviors.Storage).Bytes);
        lightBlobTxsDb.Set(tx.Hash, LightTxDecoder.Encode(tx));

        Assert.That(blobTxStorage.TryGetWithoutBlobs(tx.Hash, tx.SenderAddress!, tx.Timestamp, out Transaction elidedTx), Is.True);
        Assert.That(elidedTx.Hash, Is.EqualTo(tx.Hash));
        Assert.That(((ShardBlobNetworkWrapper)elidedTx.NetworkWrapper!).Blobs, Is.Empty);

        // the fallback read upgrades the record with the sidecar-free payload
        byte[] elidedKey = new byte[33];
        elidedKey[0] = 0x01;
        tx.Hash.BytesToArray().CopyTo(elidedKey, 1);
        Assert.That(fullBlobTxsDb.Get(elidedKey), Is.Not.Null);
    }

    [Test]
    public void Delete_should_remove_elided_payload_as_well()
    {
        BlobTxStorage blobTxStorage = new();
        EthereumEcdsa ecdsa = new(BlockchainIds.Mainnet);

        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields()
            .WithMaxFeePerGas(1.GWei)
            .WithMaxPriorityFeePerGas(1.GWei)
            .SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;

        blobTxStorage.Add(tx);
        blobTxStorage.Delete(tx.Hash, tx.Timestamp);

        Assert.That(blobTxStorage.TryGetWithoutBlobs(tx.Hash, tx.SenderAddress!, tx.Timestamp, out _), Is.False);
    }
}
