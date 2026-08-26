// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using CkzgLib;
using System;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
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
}
