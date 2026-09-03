// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using CkzgLib;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.TxDecoders;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding;

/// <summary>
/// Round-trips of the EIP-8141 frame transaction payload
/// <c>[chain_id, nonce, sender, frames, signatures, fees, blob_versioned_hashes]</c>, where
/// <c>fees = [max_priority_fee_per_gas, max_fee_per_gas, max_fee_per_blob_gas]</c>, and the
/// <c>compute_sig_hash</c> elision rule.
/// The generic transaction comparer does not cover frame fields, so they are asserted explicitly.
/// </summary>
[TestFixture]
public class FrameTxDecoderTests
{
    private const int BlobVersionedHashesDecodeCap = ShardBlobNetworkWrapperRlp.BlobCountLimit;
    private const int SignaturesDecodeCap = 1024;
    private const int FrameDataDecodeCap = 30 * 1024 * 1024;

    private static readonly TxDecoder _txDecoder = TxDecoder.Instance;

    [TestCaseSource(nameof(RoundtripCases))]
    public void Roundtrip_FrameTxPayload_PreservesAllFields(Transaction tx)
    {
        Transaction decoded = EncodeDecode(tx);

        Assert.That(decoded.Type, Is.EqualTo(TxType.FrameTx));
        Assert.That(decoded.ChainId, Is.EqualTo(tx.ChainId));
        Assert.That(decoded.Nonce, Is.EqualTo(tx.Nonce));
        AssertReferencesEqual(decoded.RecentRootReferences, tx.RecentRootReferences);
        Assert.That(decoded.NonceKeys, Is.EqualTo(tx.NonceKeys));
        // The sender is explicit in the payload — no envelope signature, no ECDSA recovery.
        Assert.That(decoded.SenderAddress, Is.EqualTo(tx.SenderAddress));
        Assert.That(decoded.GasPrice, Is.EqualTo(tx.GasPrice));
        Assert.That(decoded.DecodedMaxFeePerGas, Is.EqualTo(tx.DecodedMaxFeePerGas));
        Assert.That(decoded.MaxFeePerBlobGas, Is.EqualTo(tx.MaxFeePerBlobGas ?? UInt256.Zero));
        Assert.That(decoded.BlobVersionedHashes ?? [], Is.EqualTo(tx.BlobVersionedHashes ?? []));
        AssertFramesEqual(decoded.Frames!, tx.Frames!);
        AssertSignaturesEqual(decoded.FrameSignatures!, tx.FrameSignatures!);
    }

    [Test]
    public void Roundtrip_NetworkWrapperForm_PreservesWrapperAndConsensusHash([Values] ProofVersion version)
    {
        Transaction tx = CreateBlobCarryingFrameTx(version, blobCount: 2);

        Transaction decoded = EncodeDecode(tx, RlpBehaviors.InMempoolForm);

        ShardBlobNetworkWrapper expected = (ShardBlobNetworkWrapper)tx.NetworkWrapper!;
        ShardBlobNetworkWrapper actual = (ShardBlobNetworkWrapper)decoded.NetworkWrapper!;
        Assert.That(actual.Version, Is.EqualTo(expected.Version));
        Assert.That(actual.Blobs, Is.EqualTo(expected.Blobs));
        Assert.That(actual.Commitments, Is.EqualTo(expected.Commitments));
        Assert.That(actual.Proofs, Is.EqualTo(expected.Proofs));
        Assert.That(decoded.BlobVersionedHashes ?? [], Is.EqualTo(tx.BlobVersionedHashes ?? []));

        Assert.That(decoded.Hash, Is.EqualTo(ConsensusHash(tx)));
    }

    [Test]
    public void NetworkWrapper_DoesNotChangeConsensusEncodingOrSigHash([Values] ProofVersion version)
    {
        Transaction withWrapper = CreateBlobCarryingFrameTx(version, blobCount: 2);

        Transaction withoutWrapper = EncodeDecode(withWrapper, RlpBehaviors.None);
        withoutWrapper.NetworkWrapper = null;

        Rlp consensusWithWrapper = _txDecoder.EncodeTx(withWrapper);
        Rlp consensusWithoutWrapper = _txDecoder.EncodeTx(withoutWrapper);
        Assert.That(consensusWithWrapper.Bytes, Is.EqualTo(consensusWithoutWrapper.Bytes));
        Assert.That(FrameTxSigHash.ComputeValue(withWrapper), Is.EqualTo(FrameTxSigHash.ComputeValue(withoutWrapper)));
    }

    [Test]
    public void Roundtrip_BloblessFrameTx_InMempoolForm_HasNoWrapper()
    {
        Transaction tx = CreateFrameTx();

        Transaction decoded = EncodeDecode(tx, RlpBehaviors.InMempoolForm);

        Assert.That(decoded.NetworkWrapper, Is.Null);
        Assert.That(decoded.Hash, Is.EqualTo(ConsensusHash(tx)));
    }

    // Such a transaction would reach the blob pool with no sidecar to serve.
    [Test]
    public void Decode_BlobCarryingFrameTxWithoutWrapper_InMempoolForm_ThrowsRlpException()
    {
        Transaction tx = CreateBlobCarryingFrameTx(ProofVersion.V1, blobCount: 1);
        tx.NetworkWrapper = null;

        // The plain form is byte-identical to the consensus form.
        byte[] bytes = new byte[_txDecoder.GetLength(tx, RlpBehaviors.SkipTypedWrapping)];
        RlpWriter writer = new(bytes);
        _txDecoder.Encode(ref writer, tx, RlpBehaviors.SkipTypedWrapping);

        Assert.That(() =>
        {
            RlpReader reader = new(bytes);
            _txDecoder.Decode(ref reader, RlpBehaviors.InMempoolForm | RlpBehaviors.SkipTypedWrapping);
        }, Throws.InstanceOf<RlpException>());

        // The same bytes stay a valid consensus form: only the mempool form requires the sidecar.
        RlpReader consensusReader = new(bytes);
        Transaction decoded = _txDecoder.Decode(ref consensusReader, RlpBehaviors.SkipTypedWrapping)!;
        Assert.That(decoded.BlobVersionedHashes, Is.EqualTo(tx.BlobVersionedHashes));
    }

    [Test]
    public void Decode_NetworkWrapperWithoutSidecar_ThrowsRlpException()
    {
        // A wrapper holding only the body leaves the reader at the end of the buffer, where the peek reads out of bounds.
        byte[] payload = TypedPayload(Rlp.Encode(new[] { FrameTxBody() }));

        void Decode()
        {
            RlpReader reader = new(payload);
            _txDecoder.DecodeGuardNotNull(ref reader, RlpBehaviors.SkipTypedWrapping | RlpBehaviors.InMempoolForm);
        }

        Assert.That(Decode, Throws.InstanceOf<RlpException>());
    }

    [Test]
    public void Decode_PayloadWithTrailingSignature_Throws()
    {
        // The payload is exactly 7 fields with no envelope signature, so an appended [v, r, s] triple must be
        // rejected: decoding it with a spurious signature is a divergence that also changes the transaction hash.
        byte[] payload = TypedPayload(FrameTxBody(trailing:
            [Rlp.Encode(27L), Rlp.Encode(new byte[32]), Rlp.Encode(new byte[32])]));

        void Decode()
        {
            RlpReader reader = new(payload);
            _txDecoder.DecodeGuardNotNull(ref reader, RlpBehaviors.SkipTypedWrapping);
        }

        Assert.That(Decode, Throws.InstanceOf<RlpException>().With.Message.Contains("trailing signature"));
    }

    [Test]
    public void Decode_PayloadWithEmptySenderField_Throws()
    {
        // The sender is mandatory: a frame transaction names its payer outright rather than recovering
        // it from an envelope signature, so — unlike `to` or a frame target — an empty field has no
        // "absent" meaning and must not decode to a null sender.
        byte[] payload = TypedPayload(FrameTxBody(sender: Rlp.Encode(Array.Empty<byte>()))); // the empty string 0x80

        void Decode()
        {
            RlpReader reader = new(payload);
            _txDecoder.DecodeGuardNotNull(ref reader, RlpBehaviors.SkipTypedWrapping);
        }

        // The generic address-decode message, not a frame-specific one: "Unexpected RLP prefix" is the
        // FrameExceptionFragments.Decode fragment EF fixture rejects match on.
        Assert.That(Decode, Throws.InstanceOf<RlpException>()
            .With.Message.Contains("Unexpected RLP prefix").And.Message.Contains("decoding Address"));
    }

    [Test]
    public void ComputeSigHash_CanonicalHashSignatureBytesChange_HashUnchanged()
    {
        // Empty msg means the entry signs compute_sig_hash itself, so its raw bytes are elided
        // from the preimage — otherwise the hash would depend on the signature over it.
        Transaction first = CreateFrameTx(signatures:
            [new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, null, default, FilledBytes(65, 0x11))]);
        Transaction second = CreateFrameTx(signatures:
            [new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, null, default, FilledBytes(65, 0x22))]);

        Assert.That(FrameTxSigHash.ComputeValue(second), Is.EqualTo(FrameTxSigHash.ComputeValue(first)));
    }

    [Test]
    public void ComputeSigHash_ExplicitDigestSignatureBytesChange_HashChanges()
    {
        // A 32-byte msg signs an external digest; its raw bytes stay in the preimage.
        byte[] digest = FilledBytes(32, 0xab);
        Transaction first = CreateFrameTx(signatures:
            [new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, null, digest, FilledBytes(65, 0x11))]);
        Transaction second = CreateFrameTx(signatures:
            [new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, null, digest, FilledBytes(65, 0x22))]);

        Assert.That(FrameTxSigHash.ComputeValue(second), Is.Not.EqualTo(FrameTxSigHash.ComputeValue(first)));
    }

    [Test]
    public void ComputeSigHash_FrameFieldChanges_HashChanges()
    {
        Transaction first = CreateFrameTx(frames: [Frame(gasLimit: 100_000)]);
        Transaction second = CreateFrameTx(frames: [Frame(gasLimit: 100_001)]);

        Assert.That(FrameTxSigHash.ComputeValue(second), Is.Not.EqualTo(FrameTxSigHash.ComputeValue(first)));
    }

    // An absent list is a different envelope from an empty one, so neither may reuse the other's hash.
    [Test]
    public void ComputeSigHash_RecentRootReferencesChange_HashChanges()
    {
        Transaction none = CreateFrameTx();
        Transaction empty = CreateFrameTx();
        empty.RecentRootReferences = [];
        Transaction referencing = CreateFrameTx();
        referencing.RecentRootReferences = [Reference(slot: 7)];
        Transaction otherSlot = CreateFrameTx();
        otherSlot.RecentRootReferences = [Reference(slot: 8)];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(FrameTxSigHash.ComputeValue(empty), Is.Not.EqualTo(FrameTxSigHash.ComputeValue(none)));
            Assert.That(FrameTxSigHash.ComputeValue(referencing), Is.Not.EqualTo(FrameTxSigHash.ComputeValue(empty)));
            Assert.That(FrameTxSigHash.ComputeValue(otherSlot), Is.Not.EqualTo(FrameTxSigHash.ComputeValue(referencing)));
        }
    }

    [TestCaseSource(nameof(MalformedReferenceListCases))]
    public void Decode_MalformedRecentRootReferenceList_Throws(Rlp references) =>
        Assert.That(() => DecodeReferenceEnvelope(references), Throws.InstanceOf<RlpException>());

    // Control for the malformed cases above: without it any exception from the surrounding payload would satisfy them.
    [Test]
    public void Decode_WellFormedRecentRootReferenceList_Decodes()
    {
        Transaction tx = DecodeReferenceEnvelope(Rlp.Encode(new[]
        {
            EncodeReference(TestItem.KeccakA.BytesToArray(), 7, TestItem.KeccakB.BytesToArray())
        }));

        Assert.That(tx.RecentRootReferences, Has.Length.EqualTo(1));
    }

    private static IEnumerable<TestCaseData> MalformedReferenceListCases()
    {
        Rlp wellFormed = EncodeReference(TestItem.KeccakA.BytesToArray(), 7, TestItem.KeccakB.BytesToArray());

        yield return new TestCaseData(Rlp.Encode(new[]
        {
            EncodeReference(new byte[31], 7, TestItem.KeccakB.BytesToArray())
        })).SetName("Decode_ReferenceWithUndersizedSourceId_Throws");
        yield return new TestCaseData(Rlp.Encode(new[]
        {
            Rlp.Encode(new[] { Rlp.Encode(TestItem.KeccakA.BytesToArray()), Rlp.Encode(7L) })
        })).SetName("Decode_ReferenceMissingRoot_Throws");
        yield return new TestCaseData(Rlp.Encode(Enumerable.Repeat(wellFormed, Eip8272Constants.MaxRecentRootReferences + 1).ToArray()))
            .SetName("Decode_MoreReferencesThanTheCap_Throws");
        yield return new TestCaseData(Rlp.Encode(new[] { Rlp.OfEmptyList }))
            .SetName("Decode_EmptyListAsAReference_Throws");
        yield return new TestCaseData(Rlp.Encode(new[]
        {
            Rlp.Encode(new[]
            {
                Rlp.Encode(TestItem.KeccakA.BytesToArray()), Rlp.Encode(7L),
                Rlp.Encode(TestItem.KeccakB.BytesToArray()), Rlp.Encode(0L)
            })
        })).SetName("Decode_ReferenceWithAFourthElement_Throws");
    }

    private Transaction DecodeReferenceEnvelope(Rlp references)
    {
        RlpReader reader = new(TypedPayload(FrameTxBody(trailing: references)));
        return _txDecoder.DecodeGuardNotNull(ref reader, RlpBehaviors.SkipTypedWrapping);
    }

    /// <summary>
    /// Encodes the well-formed type-6 payload body that the hand-built payload tests start from.
    /// </summary>
    /// <param name="chainId">Replaces the chain_id field; defaults to <see cref="TestBlockchainIds.ChainId"/>.</param>
    /// <param name="sender">Replaces the sender field; defaults to <see cref="TestItem.AddressA"/>.</param>
    /// <param name="trailing">Extra elements appended after <c>blob_versioned_hashes</c>.</param>
    private static Rlp FrameTxBody(Rlp? chainId = null, Rlp? sender = null, params Rlp[] trailing) =>
        Rlp.Encode([
            chainId ?? Rlp.Encode(TestBlockchainIds.ChainId),
            Rlp.Encode(0L),                                 // nonce
            sender ?? Rlp.Encode(TestItem.AddressA.Bytes),  // sender
            Rlp.Encode(Array.Empty<Rlp>()),                 // frames
            Rlp.Encode(Array.Empty<Rlp>()),                 // signatures
            Rlp.Encode(Rlp.Encode(0L), Rlp.Encode(0L), Rlp.Encode(0L)), // fees
            Rlp.Encode(Array.Empty<Rlp>()),                 // blob_versioned_hashes
            .. trailing]);

    /// <summary>Prefixes <paramref name="rlp"/> with the frame transaction type byte.</summary>
    private static byte[] TypedPayload(Rlp rlp)
    {
        byte[] payload = new byte[1 + rlp.Length];
        payload[0] = (byte)TxType.FrameTx;
        rlp.Bytes.CopyTo(payload, 1);
        return payload;
    }

    private static Rlp EncodeReference(byte[] sourceId, ulong slot, byte[] root) =>
        Rlp.Encode(new[] { Rlp.Encode(sourceId), Rlp.Encode(slot), Rlp.Encode(root) });

    private static RecentRootReference Reference(ulong slot) =>
        new(TestItem.KeccakA.ValueHash256, slot, TestItem.KeccakB.ValueHash256);

    private static void AssertReferencesEqual(RecentRootReference[]? actual, RecentRootReference[]? expected)
    {
        if (expected is null)
        {
            Assert.That(actual, Is.Null);
            return;
        }

        Assert.That(actual, Is.Not.Null);
        Assert.That(actual!.Length, Is.EqualTo(expected.Length));
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.That(actual[i].SourceId, Is.EqualTo(expected[i].SourceId));
            Assert.That(actual[i].Slot, Is.EqualTo(expected[i].Slot));
            Assert.That(actual[i].Root, Is.EqualTo(expected[i].Root));
        }
    }

    [Test]
    public void ComputeSigHash_MaxFeePerBlobGasChanges_HashChanges()
    {
        // The blob fields are part of the signing preimage, so the signature must commit to them.
        Transaction first = CreateFrameTx();
        first.MaxFeePerBlobGas = 7;
        Transaction second = CreateFrameTx();
        second.MaxFeePerBlobGas = 8;

        Assert.That(FrameTxSigHash.ComputeValue(second), Is.Not.EqualTo(FrameTxSigHash.ComputeValue(first)));
    }

    [Test]
    public void ComputeSigHash_BlobVersionedHashesChange_HashChanges()
    {
        Transaction first = CreateFrameTx();
        first.BlobVersionedHashes = [FilledBytes(32, 0x01)];
        Transaction second = CreateFrameTx();
        second.BlobVersionedHashes = [FilledBytes(32, 0x02)];

        Assert.That(FrameTxSigHash.ComputeValue(second), Is.Not.EqualTo(FrameTxSigHash.ComputeValue(first)));
    }

    // Selecting different keys — or none at all, which is a different envelope rather than the key 0 —
    // must not reuse another transaction's signing hash.
    [Test]
    public void ComputeSigHash_NonceKeysChange_HashChanges()
    {
        Transaction legacy = CreateFrameTx();
        Transaction legacyKey = CreateFrameTx();
        legacyKey.NonceKeys = [UInt256.Zero];
        Transaction otherKey = CreateFrameTx();
        otherKey.NonceKeys = [1];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(FrameTxSigHash.ComputeValue(legacyKey), Is.Not.EqualTo(FrameTxSigHash.ComputeValue(legacy)));
            Assert.That(FrameTxSigHash.ComputeValue(otherKey), Is.Not.EqualTo(FrameTxSigHash.ComputeValue(legacyKey)));
        }
    }

    private static IEnumerable<TestCaseData> RoundtripCases()
    {
        yield return new TestCaseData(CreateFrameTx()).SetName("Roundtrip_MinimalSingleFrame");

        yield return new TestCaseData(CreateFrameTx(frames:
        [
            Frame(),
            Frame(mode: TxFrame.ModeVerify, flags: TxFrame.ApproveExecutionAndPayment, data: [1, 2, 3]),
            Frame(mode: TxFrame.ModeSender, flags: TxFrame.AtomicBatchFlag, target: TestItem.AddressB, value: 123456789, data: FilledBytes(100, 0x5a)),
            Frame(),
        ])).SetName("Roundtrip_AllModesFlagsTargetsAndData");

        yield return new TestCaseData(CreateFrameTx(frames:
        [
            Frame(gasLimit: 500_000, stateGasLimit: 183_600),
            Frame(mode: TxFrame.ModeVerify, gasLimit: 90_000, stateGasLimit: 0),
            Frame(mode: TxFrame.ModeSender, gasLimit: ulong.MaxValue - 1, stateGasLimit: 1),
        ])).SetName("Roundtrip_TwoDimensionalGasLimits");

        yield return new TestCaseData(CreateFrameTx(signatures:
        [
            new TxFrameSignature(TxFrameSignature.SchemeArbitrary, null, default, FilledBytes(11, 0x77)),
            new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, null, default, FilledBytes(TxFrameSignature.Secp256k1SignatureLength, 0x11)),
            new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, TestItem.AddressC, FilledBytes(32, 0xab), FilledBytes(TxFrameSignature.Secp256k1SignatureLength, 0x22)),
            new TxFrameSignature(TxFrameSignature.SchemeP256, TestItem.AddressD, default, FilledBytes(TxFrameSignature.P256SignatureLength, 0x33)),
        ])).SetName("Roundtrip_AllSignatureSchemes");

        Transaction emptyReferences = CreateFrameTx();
        emptyReferences.RecentRootReferences = [];
        yield return new TestCaseData(emptyReferences).SetName("Roundtrip_EmptyRecentRootReferenceList");

        Transaction referencing = CreateFrameTx();
        referencing.RecentRootReferences = [Reference(slot: 0), Reference(slot: ulong.MaxValue)];
        yield return new TestCaseData(referencing).SetName("Roundtrip_RecentRootReferences");

        Transaction keyed = CreateFrameTx();
        keyed.NonceKeys = [UInt256.Zero];
        yield return new TestCaseData(keyed).SetName("Roundtrip_KeyedNonceEnvelope_LegacyKeyOnly");

        Transaction multiKeyed = CreateFrameTx();
        multiKeyed.NonceKeys = [1, UInt256.MaxValue];
        yield return new TestCaseData(multiKeyed).SetName("Roundtrip_KeyedNonceEnvelope_MultipleKeys");

        Transaction blobCarrying = CreateFrameTx();
        blobCarrying.MaxFeePerBlobGas = 7;
        blobCarrying.BlobVersionedHashes = [FilledBytes(32, 0x01), FilledBytes(32, 0x02)];
        yield return new TestCaseData(blobCarrying).SetName("Roundtrip_WithBlobFields");
    }

    // Decoding `c1 c0` as the set [0] would move the sequence into the sender's account nonce.
    [Test]
    public void Decode_NonceKeyEncodedAsAnEmptyList_Throws()
    {
        Transaction keyed = CreateFrameTx();
        keyed.NonceKeys = [1];
        byte[] bytes = new byte[_txDecoder.GetLength(keyed, RlpBehaviors.None)];
        RlpWriter writer = new(bytes);
        _txDecoder.Encode(ref writer, keyed);
        // The one-byte key `01` and the empty list `c0` occupy the same slot, so no length changes.
        int keyIndex = Array.IndexOf(bytes, (byte)0xc1) + 1;
        bytes[keyIndex] = 0xc0;

        Assert.That(() => { RlpReader reader = new(bytes); _txDecoder.Decode(ref reader); }, Throws.InstanceOf<RlpException>());
    }

    [Test]
    public void Decode_MoreNonceKeysThanTheCap_Throws()
    {
        Transaction keyed = CreateFrameTx();
        keyed.NonceKeys = [.. Enumerable.Range(1, Eip8250Constants.MaxNonceKeys + 1).Select(static i => (UInt256)i)];

        Assert.That(() => EncodeDecode(keyed), Throws.InstanceOf<RlpException>());
    }

    [TestCase(Eip8141Constants.MaxFrames, false)]
    [TestCase(Eip8141Constants.MaxFrames + 1, true)]
    public void Decode_BoundsTheFrameCount(int frameCount, bool rejected)
    {
        Transaction tx = CreateFrameTx(frames: [.. Enumerable.Range(0, frameCount).Select(static _ => Frame())]);

        if (rejected)
        {
            Assert.That(() => EncodeDecode(tx), Throws.InstanceOf<RlpLimitException>());
        }
        else
        {
            Assert.That(EncodeDecode(tx).Frames!.Length, Is.EqualTo(frameCount));
        }
    }

    // Mirrors the blob tx cap, so the two decoders reject an oversized hash list at the same count.
    [TestCase(BlobVersionedHashesDecodeCap, false)]
    [TestCase(BlobVersionedHashesDecodeCap + 1, true)]
    public void Decode_BoundsTheBlobVersionedHashCount(int hashCount, bool rejected)
    {
        Transaction tx = CreateFrameTx();
        tx.MaxFeePerBlobGas = 1;
        tx.BlobVersionedHashes = [.. Enumerable.Range(0, hashCount).Select(static _ => FilledBytes(Hash256.Size, 0x01))];

        if (rejected)
        {
            Assert.That(() => EncodeDecode(tx), Throws.InstanceOf<RlpLimitException>());
        }
        else
        {
            Assert.That(EncodeDecode(tx).BlobVersionedHashes!.Length, Is.EqualTo(hashCount));
        }
    }

    [TestCase(SignaturesDecodeCap, false)]
    [TestCase(SignaturesDecodeCap + 1, true)]
    public void Decode_BoundsTheSignatureCount(int signatureCount, bool rejected)
    {
        Transaction tx = CreateFrameTx(signatures:
            [.. Enumerable.Range(0, signatureCount).Select(static _ =>
                new TxFrameSignature(TxFrameSignature.SchemeArbitrary, null, default, default))]);

        if (rejected)
        {
            Assert.That(() => EncodeDecode(tx), Throws.InstanceOf<RlpLimitException>());
        }
        else
        {
            Assert.That(EncodeDecode(tx).FrameSignatures!.Length, Is.EqualTo(signatureCount));
        }
    }

    [Test]
    public void Decode_ChainIdAtTwoToThe64_ThrowsRatherThanTruncatingToU64()
    {
        byte[] payload = TypedPayload(FrameTxBody(chainId: Rlp.Encode((UInt256)ulong.MaxValue + 1)));

        void Decode()
        {
            RlpReader reader = new(payload);
            _txDecoder.DecodeGuardNotNull(ref reader, RlpBehaviors.SkipTypedWrapping);
        }

        Assert.That(Decode, Throws.InstanceOf<RlpException>().With.Message.Contains("Unexpected length of integer"));
    }

    [TestCase(FrameDataDecodeCap, false)]
    [TestCase(FrameDataDecodeCap + 1, true)]
    public void Decode_BoundsTheFrameDataLength(int dataLength, bool rejected)
    {
        Transaction tx = CreateFrameTx(frames: [Frame(data: new byte[dataLength])]);

        if (rejected)
        {
            Assert.That(() => EncodeDecode(tx), Throws.InstanceOf<RlpLimitException>());
        }
        else
        {
            Assert.That(EncodeDecode(tx).Frames![0].Data.Length, Is.EqualTo(dataLength));
        }
    }

    private static Transaction EncodeDecode(Transaction tx, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        byte[] bytes = new byte[_txDecoder.GetLength(tx, rlpBehaviors)];
        RlpWriter writer = new(bytes);
        _txDecoder.Encode(ref writer, tx, rlpBehaviors);
        RlpReader reader = new(bytes);
        return _txDecoder.Decode(ref reader, rlpBehaviors)!;
    }

    private static Hash256 ConsensusHash(Transaction tx)
    {
        // The consensus form ignores any network wrapper: keccak(type || rlp(tx_payload_body)).
        byte[] bytes = new byte[_txDecoder.GetLength(tx, RlpBehaviors.SkipTypedWrapping)];
        RlpWriter writer = new(bytes);
        _txDecoder.Encode(ref writer, tx, RlpBehaviors.SkipTypedWrapping);
        return Keccak.Compute(bytes);
    }

    private static Transaction CreateBlobCarryingFrameTx(ProofVersion version, int blobCount)
    {
        if (!KzgPolynomialCommitments.IsInitialized)
        {
            KzgPolynomialCommitments.InitializeAsync().Wait();
        }

        IBlobProofsManager proofsManager = IBlobProofsManager.For(version);
        ShardBlobNetworkWrapper wrapper = proofsManager.AllocateWrapper(
            [.. Enumerable.Range(1, blobCount).Select(i =>
            {
                byte[] blob = new byte[Ckzg.BytesPerBlob];
                blob[0] = (byte)(i % 256);
                return blob;
            })]);
        proofsManager.ComputeProofsAndCommitments(wrapper);

        Transaction tx = CreateFrameTx();
        tx.MaxFeePerBlobGas = 1;
        tx.BlobVersionedHashes = proofsManager.ComputeHashes(wrapper);
        tx.NetworkWrapper = wrapper;
        return tx;
    }

    private static void AssertFramesEqual(TxFrame[] actual, TxFrame[] expected)
    {
        Assert.That(actual.Length, Is.EqualTo(expected.Length));
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.That(actual[i].Mode, Is.EqualTo(expected[i].Mode), $"frame {i} mode");
            Assert.That(actual[i].Flags, Is.EqualTo(expected[i].Flags), $"frame {i} flags");
            Assert.That(actual[i].Target, Is.EqualTo(expected[i].Target), $"frame {i} target");
            Assert.That(actual[i].ExecutionGasLimit, Is.EqualTo(expected[i].ExecutionGasLimit), $"frame {i} execution gas limit");
            Assert.That(actual[i].StateGasLimit, Is.EqualTo(expected[i].StateGasLimit), $"frame {i} state gas limit");
            Assert.That(actual[i].Value, Is.EqualTo(expected[i].Value), $"frame {i} value");
            Assert.That(actual[i].Data.ToArray(), Is.EqualTo(expected[i].Data.ToArray()), $"frame {i} data");
        }
    }

    private static void AssertSignaturesEqual(TxFrameSignature[] actual, TxFrameSignature[] expected)
    {
        Assert.That(actual.Length, Is.EqualTo(expected.Length));
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.That(actual[i].Scheme, Is.EqualTo(expected[i].Scheme), $"signature {i} scheme");
            Assert.That(actual[i].Signer, Is.EqualTo(expected[i].Signer), $"signature {i} signer");
            Assert.That(actual[i].Msg.ToArray(), Is.EqualTo(expected[i].Msg.ToArray()), $"signature {i} msg");
            Assert.That(actual[i].Signature.ToArray(), Is.EqualTo(expected[i].Signature.ToArray()), $"signature {i} bytes");
        }
    }

    private static Transaction CreateFrameTx(TxFrame[]? frames = null, TxFrameSignature[]? signatures = null) =>
        new()
        {
            Type = TxType.FrameTx,
            ChainId = TestBlockchainIds.ChainId,
            Nonce = 42,
            SenderAddress = TestItem.AddressA,
            Frames = frames ?? [Frame()],
            FrameSignatures = signatures ?? [],
            GasPrice = 1.GWei, // max_priority_fee_per_gas
            DecodedMaxFeePerGas = 30.GWei,
        };

    private static TxFrame Frame(byte mode = TxFrame.ModeDefault, byte flags = 0, Address? target = null, ulong gasLimit = 100_000, ulong stateGasLimit = 0, UInt256 value = default, byte[]? data = null) =>
        new(mode, flags, target, gasLimit, stateGasLimit, value, data ?? Array.Empty<byte>());

    private static byte[] FilledBytes(int length, byte fill)
    {
        byte[] bytes = new byte[length];
        bytes.AsSpan().Fill(fill);
        return bytes;
    }
}
