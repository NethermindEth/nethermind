// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
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

    // Zero is a deadline, not an absent field: the trailing fields are positional, so the two must decode apart.
    [TestCase(null)]
    [TestCase(0ul)]
    [TestCase(1_000ul)]
    public void Round_trip_preserves_the_expiry_deadline(ulong? deadline)
    {
        Transaction tx = BlobCarryingTx(TxType.FrameTx, deadline);

        Assert.That(LightTxDecoder.Decode(LightTxDecoder.Encode(tx)).PersistedExpiryDeadline, Is.EqualTo(deadline));
    }

    // Each trailing group is written only when it applies, so all four layouts have to decode apart on the
    // scalar-vs-sequence distinction alone — otherwise the payer group is read as a deadline, or missed entirely.
    [TestCase(null, false, TestName = "neither the deadline nor the payer pair")]
    [TestCase(1_000ul, false, TestName = "the deadline alone")]
    [TestCase(null, true, TestName = "the payer pair alone")]
    [TestCase(1_000ul, true, TestName = "the deadline and the payer pair")]
    public void Round_trip_separates_the_optional_trailing_groups(ulong? deadline, bool withPayer)
    {
        Transaction tx = BlobCarryingTx(TxType.FrameTx, deadline);
        if (withPayer)
        {
            tx.PayerAddress = TestItem.AddressB;
            tx.PayerExposure = 12_345;
        }

        LightTransaction decoded = LightTxDecoder.Decode(LightTxDecoder.Encode(tx));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.PersistedExpiryDeadline, Is.EqualTo(deadline));
            Assert.That(decoded.PayerAddress, Is.EqualTo(withPayer ? TestItem.AddressB : null));
            Assert.That(decoded.PayerExposure, Is.EqualTo(withPayer ? (UInt256)12_345 : null));
        }
    }

    // The reservation the pool's per-payer ledger is rebuilt from, so it has to survive at its exact value.
    // Zero is the RLP empty string, which a raw byte read would hand back as 128.
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(12_345)]
    public void Round_trip_preserves_the_reserved_payer_exposure(int exposure)
    {
        Transaction tx = BlobCarryingTx(TxType.FrameTx);
        tx.PayerAddress = TestItem.AddressB;
        tx.PayerExposure = (UInt256)exposure;

        LightTransaction decoded = LightTxDecoder.Decode(LightTxDecoder.Encode(tx));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.PayerAddress, Is.EqualTo(TestItem.AddressB));
            Assert.That(decoded.PayerExposure, Is.EqualTo((UInt256)exposure));
        }
    }

    // A resolved payer that never reached the exposure gate holds no reservation, which this ledger cannot tell
    // from a zero one: reserving, releasing and restoring zero are all no-ops. The payer itself must still survive.
    [Test]
    public void Round_trip_of_a_payer_without_a_reservation_keeps_the_payer()
    {
        Transaction tx = BlobCarryingTx(TxType.FrameTx);
        tx.PayerAddress = TestItem.AddressB;
        tx.PayerExposure = null;

        LightTransaction decoded = LightTxDecoder.Decode(LightTxDecoder.Encode(tx));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.PayerAddress, Is.EqualTo(TestItem.AddressB));
            Assert.That(decoded.PayerExposure, Is.EqualTo(UInt256.Zero));
        }
    }

    // A plain blob tx has no payer, so the pair must not be written at all and its record must keep the exact
    // layout every already-persisted one has — the truncation cases above are what read those back.
    [Test]
    public void The_payer_pair_is_written_only_for_a_resolved_payer()
    {
        Transaction payerless = BlobCarryingTx(TxType.Blob);
        Transaction withPayer = BlobCarryingTx(TxType.Blob);
        withPayer.PayerAddress = TestItem.AddressB;
        withPayer.PayerExposure = 1;

        int grownBy = LightTxDecoder.Encode(withPayer).Length - LightTxDecoder.Encode(payerless).Length;

        // A 1-byte list header over 21 bytes of address and 1 of reservation, and nothing when there is no payer.
        Assert.That(grownBy, Is.EqualTo(23));
    }

    private static Transaction BlobCarryingTx(TxType type, ulong? deadline = null)
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

        if (deadline is not null)
        {
            byte[] expiryData = new byte[Eip8141Constants.ExpiryDataLength];
            BinaryPrimitives.WriteUInt64BigEndian(expiryData, deadline.Value);
            tx.Frames = [.. tx.Frames!, new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveScopeNone, Eip8141Constants.ExpiryVerifierAddress, gasLimit: 50_000, UInt256.Zero, expiryData)];
        }

        tx.Hash = tx.CalculateHash();
        return tx;
    }
}
