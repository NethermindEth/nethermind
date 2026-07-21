// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Security.Cryptography;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// The spec <c>validate_signature</c> matrix: every protocol-validated signature must verify before
/// any frame executes. SECP256K1 recovers and compares against the resolved signer (explicit or
/// tx.sender); ARBITRARY entries pass pre-flight (their witness is verified by frame code); P256
/// checks the key-derived signer then verifies through the secp256r1 (P256VERIFY) precompile.
/// </summary>
[TestFixture]
public class FrameTxSignatureValidatorTests
{
    private readonly IEthereumEcdsa _ethereumEcdsa = new EthereumEcdsa(TestBlockchainIds.ChainId);
    private readonly Ecdsa _ecdsa = new();
    private readonly IReleaseSpec _spec = Substitute.For<IReleaseSpec>();

    [Test]
    public void Validate_NoSignatures_ReturnsTrue()
    {
        Transaction tx = CreateFrameTx();

        Assert.That(Validate(tx, out string? error), Is.True);
        Assert.That(error, Is.Null);
    }

    [Test]
    public void Validate_Secp256k1SignsCanonicalHashWithExplicitSigner_ReturnsTrue()
    {
        Transaction tx = CreateFrameTx();
        tx.FrameSignatures = [Secp256k1Entry(tx, TestItem.PrivateKeyB, signer: TestItem.PrivateKeyB.Address)];

        Assert.That(Validate(tx, out string? error), Is.True);
        Assert.That(error, Is.Null);
    }

    [Test]
    public void Validate_Secp256k1WithAbsentSigner_ResolvesToTxSender()
    {
        // "If absent, tx.sender is used" — the sender key must have produced the signature.
        Transaction tx = CreateFrameTx(sender: TestItem.PrivateKeyA.Address);
        tx.FrameSignatures = [Secp256k1Entry(tx, TestItem.PrivateKeyA, signer: null)];

        Assert.That(Validate(tx, out string? error), Is.True);
        Assert.That(error, Is.Null);
    }

    [Test]
    public void Validate_Secp256k1SignedByDifferentKey_ReturnsFalse()
    {
        Transaction tx = CreateFrameTx();
        tx.FrameSignatures = [Secp256k1Entry(tx, TestItem.PrivateKeyB, signer: TestItem.PrivateKeyC.Address)];

        Assert.That(Validate(tx, out string? error), Is.False);
        Assert.That(error, Is.EqualTo(FrameTxSignatureValidator.InvalidSignature));
    }

    [Test]
    public void Validate_Secp256k1WrongLength_ReturnsFalse()
    {
        Transaction tx = CreateFrameTx();
        tx.FrameSignatures = [new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, TestItem.AddressB, default, new byte[64])];

        Assert.That(Validate(tx, out string? error), Is.False);
        Assert.That(error, Is.EqualTo(FrameTxSignatureValidator.InvalidSignatureLength));
    }

    [Test]
    public void Validate_Secp256k1WithLegacy27RecoveryId_RejectedAsNonCanonical()
    {
        // EIP8141-GAP: the v encoding is enforced strictly as a 0/1 recovery id so every
        // signature has exactly one valid byte encoding; the legacy 27/28 form is rejected.
        Transaction tx = CreateFrameTx();
        TxFrameSignature canonical = Secp256k1Entry(tx, TestItem.PrivateKeyB, signer: TestItem.PrivateKeyB.Address);
        byte[] legacyIdBytes = canonical.Signature.ToArray();
        legacyIdBytes[0] += 27;
        tx.FrameSignatures = [new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, TestItem.PrivateKeyB.Address, default, legacyIdBytes)];

        Assert.That(Validate(tx, out string? error), Is.False);
        Assert.That(error, Is.EqualTo(FrameTxSignatureValidator.NonCanonicalSignature));
    }

    [Test]
    public void Validate_Secp256k1WithHighS_RejectedAsNonCanonical()
    {
        // The flipped (v ^ 1, r, N - s) form recovers the same address but is non-canonical.
        Transaction tx = CreateFrameTx();
        TxFrameSignature canonical = Secp256k1Entry(tx, TestItem.PrivateKeyB, signer: TestItem.PrivateKeyB.Address);
        byte[] highSBytes = canonical.Signature.ToArray();
        highSBytes[0] ^= 1;
        UInt256 s = new(highSBytes.AsSpan(33, 32), isBigEndian: true);
        UInt256 highS = SecP256k1Curve.N - s;
        highS.ToBigEndian(highSBytes.AsSpan(33, 32));
        tx.FrameSignatures = [new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, TestItem.PrivateKeyB.Address, default, highSBytes)];

        Assert.That(Validate(tx, out string? error), Is.False);
        Assert.That(error, Is.EqualTo(FrameTxSignatureValidator.NonCanonicalSignature));
    }

    [Test]
    public void Validate_Secp256k1SignsExplicitDigest_ReturnsTrue()
    {
        Transaction tx = CreateFrameTx();
        ValueHash256 digest = Keccak.Compute("external message");
        Signature signature = _ecdsa.Sign(TestItem.PrivateKeyB, in digest);
        tx.FrameSignatures =
        [
            new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, TestItem.PrivateKeyB.Address, digest.ToByteArray(), ToVrs(signature)),
        ];

        Assert.That(Validate(tx, out string? error), Is.True);
        Assert.That(error, Is.Null);
    }

    [Test]
    public void Validate_Secp256k1DigestSignatureCheckedAgainstDifferentDigest_ReturnsFalse()
    {
        Transaction tx = CreateFrameTx();
        ValueHash256 signedDigest = Keccak.Compute("signed message");
        ValueHash256 claimedDigest = Keccak.Compute("different message");
        Signature signature = _ecdsa.Sign(TestItem.PrivateKeyB, in signedDigest);
        tx.FrameSignatures =
        [
            new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, TestItem.PrivateKeyB.Address, claimedDigest.ToByteArray(), ToVrs(signature)),
        ];

        Assert.That(Validate(tx, out string? error), Is.False);
        Assert.That(error, Is.EqualTo(FrameTxSignatureValidator.InvalidSignature));
    }

    [Test]
    public void Validate_NonEmptyMsgShorterThanDigest_RejectedInsteadOfOverReading()
    {
        // eth_call/estimateGas/simulate reach the validator with an unvalidated FrameTx, so a
        // non-empty Msg that is not a 32-byte digest must be rejected here — feeding it to
        // ValueHash256(span), which reads 32 bytes unchecked, would over-read the buffer.
        Transaction tx = CreateFrameTx();
        tx.FrameSignatures =
        [
            new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, TestItem.AddressB, new byte[5], new byte[TxFrameSignature.Secp256k1SignatureLength]),
        ];

        Assert.That(Validate(tx, out string? error), Is.False);
        Assert.That(error, Is.EqualTo(FrameTxSignatureValidator.InvalidMsgLength));
    }

    [Test]
    public void Validate_ArbitraryEntry_PassesPreFlight()
    {
        // ARBITRARY witnesses are verified by frame code, not by the protocol pre-flight.
        Transaction tx = CreateFrameTx();
        tx.FrameSignatures = [new TxFrameSignature(TxFrameSignature.SchemeArbitrary, null, default, new byte[] { 0xde, 0xad })];

        Assert.That(Validate(tx, out string? error), Is.True);
        Assert.That(error, Is.Null);
    }

    [Test]
    public void Validate_P256SignerMismatchesPublicKey_ReturnsFalse()
    {
        // The signer address must be keccak256(qx || qy)[12:] — checked even while verification
        // itself is deferred.
        Transaction tx = CreateFrameTx();
        tx.FrameSignatures = [new TxFrameSignature(TxFrameSignature.SchemeP256, TestItem.AddressB, default, new byte[TxFrameSignature.P256SignatureLength])];

        Assert.That(Validate(tx, out string? error), Is.False);
        Assert.That(error, Is.EqualTo(FrameTxSignatureValidator.InvalidP256Signer));
    }

    [Test]
    public void Validate_P256MatchingSignerBadSignature_RejectedAsInvalid()
    {
        Transaction tx = CreateFrameTx();
        byte[] raw = new byte[TxFrameSignature.P256SignatureLength];
        raw.AsSpan(64).Fill(0x42); // qx || qy — a matching signer over non-verifying signature bytes
        Address derivedSigner = new(Keccak.Compute(raw.AsSpan(64)).Bytes[12..]);
        tx.FrameSignatures = [new TxFrameSignature(TxFrameSignature.SchemeP256, derivedSigner, default, raw)];

        Assert.That(Validate(tx, out string? error), Is.False);
        Assert.That(error, Is.EqualTo(FrameTxSignatureValidator.InvalidSignature));
    }

    [Test]
    public void Validate_P256ValidSignature_ReturnsTrue()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        ECParameters pub = key.ExportParameters(includePrivateParameters: false);
        byte[] qx = Pad32(pub.Q.X!);
        byte[] qy = Pad32(pub.Q.Y!);
        Address signer = new(Keccak.Compute([.. qx, .. qy]).Bytes[12..]);

        Transaction tx = CreateFrameTx();
        // Install the placeholder (scheme/signer/msg) so the sig hash is fixed before signing.
        tx.FrameSignatures = [new TxFrameSignature(TxFrameSignature.SchemeP256, signer, default, default)];
        ValueHash256 sigHash = FrameTxSigHash.ComputeValue(tx);

        byte[] rs = key.SignHash(sigHash.Bytes); // IEEE P1363: r || s
        byte[] raw = new byte[TxFrameSignature.P256SignatureLength];
        rs.CopyTo(raw.AsSpan(0));
        qx.CopyTo(raw.AsSpan(64));
        qy.CopyTo(raw.AsSpan(96));
        tx.FrameSignatures = [new TxFrameSignature(TxFrameSignature.SchemeP256, signer, default, raw)];

        Assert.That(Validate(tx, out string? error), Is.True, error);
    }

    [Test]
    public void Validate_P256WithoutPrecompile_RejectedAsNotSupported()
    {
        Transaction tx = CreateFrameTx();
        byte[] raw = new byte[TxFrameSignature.P256SignatureLength];
        raw.AsSpan(64).Fill(0x42);
        Address derivedSigner = new(Keccak.Compute(raw.AsSpan(64)).Bytes[12..]);
        tx.FrameSignatures = [new TxFrameSignature(TxFrameSignature.SchemeP256, derivedSigner, default, raw)];

        bool ok = FrameTxSignatureValidator.Validate(tx, FrameTxSigHash.ComputeValue(tx), _ethereumEcdsa, p256Precompile: null, _spec, out string? error);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ok, Is.False);
            Assert.That(error, Is.EqualTo(FrameTxSignatureValidator.P256NotSupported));
        }
    }

    [Test]
    public void Validate_SecondEntryInvalid_WholeValidationFails()
    {
        Transaction tx = CreateFrameTx();
        TxFrameSignature invalid = new(TxFrameSignature.SchemeSecp256k1, TestItem.AddressC, default, new byte[65]);
        tx.FrameSignatures =
        [
            new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, TestItem.PrivateKeyB.Address, default, default),
            invalid,
        ];
        ValueHash256 sigHash = FrameTxSigHash.ComputeValue(tx);
        Signature signature = _ecdsa.Sign(TestItem.PrivateKeyB, in sigHash);
        tx.FrameSignatures =
        [
            new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, TestItem.PrivateKeyB.Address, default, ToVrs(signature)),
            invalid,
        ];

        Assert.That(Validate(tx, out string? error), Is.False);
        Assert.That(error, Is.Not.Null);
    }

    private bool Validate(Transaction tx, out string? error) =>
        FrameTxSignatureValidator.Validate(tx, FrameTxSigHash.ComputeValue(tx), _ethereumEcdsa, SecP256r1Precompile.Instance, _spec, out error);

    private TxFrameSignature Secp256k1Entry(Transaction tx, PrivateKey key, Address? signer)
    {
        // compute_sig_hash covers the signature entries (scheme/signer/msg) and elides only the
        // raw bytes of canonical-hash entries, so the hash is computed with the entry installed.
        tx.FrameSignatures = [new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, signer, default, default)];
        ValueHash256 sigHash = FrameTxSigHash.ComputeValue(tx);
        Signature signature = _ecdsa.Sign(key, in sigHash);
        return new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, signer, default, ToVrs(signature));
    }

    private static byte[] ToVrs(Signature signature)
    {
        byte[] bytes = new byte[TxFrameSignature.Secp256k1SignatureLength];
        bytes[0] = signature.RecoveryId; // strict yParity encoding (0/1)
        signature.Bytes.CopyTo(bytes.AsSpan(1));
        return bytes;
    }

    private static byte[] Pad32(byte[] value)
    {
        if (value.Length == 32) return value;
        byte[] padded = new byte[32];
        value.CopyTo(padded.AsSpan(32 - value.Length));
        return padded;
    }

    private static Transaction CreateFrameTx(Address? sender = null) =>
        new()
        {
            Type = TxType.FrameTx,
            ChainId = TestBlockchainIds.ChainId,
            Nonce = 0,
            SenderAddress = sender ?? TestItem.AddressA,
            Frames = [new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, null, 100_000, default, default)],
            FrameSignatures = [],
            GasPrice = 1,
            DecodedMaxFeePerGas = 100,
        };
}
