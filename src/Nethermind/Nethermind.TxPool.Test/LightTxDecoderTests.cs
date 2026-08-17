// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.TxPool.Test;

/// <summary>Backward compatibility of the persisted <see cref="Nethermind.Db.BlobTxsColumns.LightBlobTxs"/> record;
/// a record that fails to decode breaks pool startup.</summary>
[TestFixture]
public class LightTxDecoderTests
{
    [TestCase(TxType.Blob)]
    [TestCase(TxType.FrameTx)]
    public void Round_trip_preserves_the_type_and_proof_version(TxType type)
    {
        Transaction tx = BlobCarryingTx(type);

        LightTransaction decoded = LightTxDecoder.Decode(LightTxDecoder.Encode(tx));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.Type, Is.EqualTo(type));
            Assert.That(decoded.ProofVersion, Is.EqualTo(ProofVersion.V1));
            Assert.That(decoded.SenderAddress, Is.EqualTo(tx.SenderAddress));
            Assert.That(decoded.Nonce, Is.EqualTo(tx.Nonce));
            Assert.That(decoded.Hash, Is.EqualTo(tx.Hash));
            Assert.That(decoded.PoolIndex, Is.EqualTo(tx.PoolIndex));
            Assert.That(decoded.BlobVersionedHashes, Is.EqualTo(tx.BlobVersionedHashes));
        }
    }

    // Both trailing fields are one RLP byte, so dropping them reproduces the two older record layouts.
    [TestCase(1, ProofVersion.V1, TestName = "Legacy_record_without_the_type_field_decodes_as_a_blob_tx")]
    [TestCase(2, default(ProofVersion), TestName = "Legacy_record_without_the_proof_version_or_type_fields_decodes_as_a_blob_tx")]
    public void Legacy_record_decodes_as_a_blob_tx(int droppedTrailingFields, ProofVersion expectedProofVersion)
    {
        byte[] full = LightTxDecoder.Encode(BlobCarryingTx(TxType.Blob));

        LightTransaction decoded = LightTxDecoder.Decode(full[..^droppedTrailingFields]);

        Assert.That(decoded.Type, Is.EqualTo(TxType.Blob));
        Assert.That(decoded.ProofVersion, Is.EqualTo(expectedProofVersion));
    }

    // ProofVersion.V0 encodes as the RLP empty string, which a raw byte read returns as 128.
    [TestCase(ProofVersion.V0)]
    [TestCase(ProofVersion.V1)]
    public void Round_trip_preserves_the_proof_version(ProofVersion version)
    {
        Transaction tx = BlobCarryingTx(TxType.Blob);
        tx.NetworkWrapper = new ShardBlobNetworkWrapper([[1]], [[2]], [[3]], version);

        Assert.That(LightTxDecoder.Decode(LightTxDecoder.Encode(tx)).ProofVersion, Is.EqualTo(version));
    }

    private static Transaction BlobCarryingTx(TxType type)
    {
        byte[][] versionedHashes = [new byte[32]];
        Transaction tx = new()
        {
            Type = type,
            ChainId = TestBlockchainIds.ChainId,
            SenderAddress = TestItem.AddressA,
            Nonce = 7,
            GasLimit = 1_000_000,
            GasPrice = 1,
            DecodedMaxFeePerGas = 2,
            MaxFeePerBlobGas = 3,
            BlobVersionedHashes = versionedHashes,
            Value = 5,
            PoolIndex = 11,
            Timestamp = 42,
            NetworkWrapper = new ShardBlobNetworkWrapper([[1]], [[2]], [[3]], ProofVersion.V1),
        };

        if (type == TxType.FrameTx)
        {
            tx.Frames = [new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit: 100_000, UInt256.Zero, default)];
            tx.FrameSignatures = [];
        }

        tx.Hash = tx.CalculateHash();
        return tx;
    }
}
