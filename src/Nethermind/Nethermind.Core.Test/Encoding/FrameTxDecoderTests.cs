// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding;

/// <summary>
/// Round-trips of the EIP-8141 frame transaction payload
/// <c>[chain_id, nonce, sender, frames, signatures, max_priority_fee_per_gas, max_fee_per_gas,
/// max_fee_per_blob_gas, blob_versioned_hashes]</c> and the <c>compute_sig_hash</c> elision rule.
/// The generic transaction comparer predates frames, so fields are asserted explicitly.
/// </summary>
[TestFixture]
public class FrameTxDecoderTests
{
    private static readonly TxDecoder _txDecoder = TxDecoder.Instance;

    [TestCaseSource(nameof(RoundtripCases))]
    public void Roundtrip_FrameTxPayload_PreservesAllFields(Transaction tx)
    {
        Transaction decoded = EncodeDecode(tx);

        Assert.That(decoded.Type, Is.EqualTo(TxType.FrameTx));
        Assert.That(decoded.ChainId, Is.EqualTo(tx.ChainId));
        Assert.That(decoded.Nonce, Is.EqualTo(tx.Nonce));
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

        yield return new TestCaseData(CreateFrameTx(signatures:
        [
            new TxFrameSignature(TxFrameSignature.SchemeArbitrary, null, default, FilledBytes(11, 0x77)),
            new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, null, default, FilledBytes(TxFrameSignature.Secp256k1SignatureLength, 0x11)),
            new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, TestItem.AddressC, FilledBytes(32, 0xab), FilledBytes(TxFrameSignature.Secp256k1SignatureLength, 0x22)),
            new TxFrameSignature(TxFrameSignature.SchemeP256, TestItem.AddressD, default, FilledBytes(TxFrameSignature.P256SignatureLength, 0x33)),
        ])).SetName("Roundtrip_AllSignatureSchemes");

        Transaction blobCarrying = CreateFrameTx();
        blobCarrying.MaxFeePerBlobGas = 7;
        blobCarrying.BlobVersionedHashes = [FilledBytes(32, 0x01), FilledBytes(32, 0x02)];
        yield return new TestCaseData(blobCarrying).SetName("Roundtrip_WithBlobFields");
    }

    private static Transaction EncodeDecode(Transaction tx)
    {
        byte[] bytes = new byte[_txDecoder.GetLength(tx, RlpBehaviors.None)];
        RlpWriter writer = new(bytes);
        _txDecoder.Encode(ref writer, tx);
        RlpReader reader = new(bytes);
        return _txDecoder.Decode(ref reader)!;
    }

    private static void AssertFramesEqual(TxFrame[] actual, TxFrame[] expected)
    {
        Assert.That(actual.Length, Is.EqualTo(expected.Length));
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.That(actual[i].Mode, Is.EqualTo(expected[i].Mode), $"frame {i} mode");
            Assert.That(actual[i].Flags, Is.EqualTo(expected[i].Flags), $"frame {i} flags");
            Assert.That(actual[i].Target, Is.EqualTo(expected[i].Target), $"frame {i} target");
            Assert.That(actual[i].GasLimit, Is.EqualTo(expected[i].GasLimit), $"frame {i} gas limit");
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

    private static TxFrame Frame(byte mode = TxFrame.ModeDefault, byte flags = 0, Address? target = null, ulong gasLimit = 100_000, UInt256 value = default, byte[]? data = null) =>
        new(mode, flags, target, gasLimit, value, data ?? Array.Empty<byte>());

    private static byte[] FilledBytes(int length, byte fill)
    {
        byte[] bytes = new byte[length];
        bytes.AsSpan().Fill(fill);
        return bytes;
    }
}
