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

/// <summary>The EIP-8141 protocol verifiers, <c>secp256k1_verifier</c>, <c>p256_verifier</c> and
/// <c>leansphincs_verifier</c>, and the witness shape checks that precede them.</summary>
[TestFixture]
public class FrameTxClaimVerifierTests
{
    private readonly IEthereumEcdsa _ethereumEcdsa = new EthereumEcdsa(TestBlockchainIds.ChainId);
    private readonly Ecdsa _ecdsa = new();
    private readonly IReleaseSpec _spec = Substitute.For<IReleaseSpec>();

    [Test]
    public void Verify_NoClaims_ReturnsTrue()
    {
        Transaction tx = CreateFrameTx();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Verify(tx, out string? error), Is.True);
            Assert.That(error, Is.Null);
        }
    }

    [TestCase(false, TestName = "Verify_Secp256k1ClaimKeyedToTheSender_ReturnsTrue")]
    [TestCase(true, TestName = "Verify_Secp256k1ClaimWithExplicitKey_ReturnsTrue")]
    public void Verify_Secp256k1ClaimOverTheSigHash_ReturnsTrue(bool explicitKey)
    {
        Transaction tx = CreateFrameTx(sender: TestItem.PrivateKeyB.Address);
        SignSecp256k1(tx, TestItem.PrivateKeyB, explicitKey ? TestItem.PrivateKeyB.Address : null);

        Assert.That(Verify(tx, out string? error), Is.True, error);
    }

    [Test]
    public void Verify_BlobFieldTamperedAfterSigning_ReturnsFalse()
    {
        // The signature hash covers the whole body, so changing a blob field after signing invalidates the claim.
        Transaction tx = CreateFrameTx(sender: TestItem.PrivateKeyB.Address);
        tx.BlobVersionedHashes = [BlobVersionedHash(0x01)];
        SignSecp256k1(tx, TestItem.PrivateKeyB, null);

        tx.BlobVersionedHashes = [BlobVersionedHash(0x02)];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Verify(tx, out string? error), Is.False);
            Assert.That(error, Is.EqualTo(FrameTxClaimVerifier.InvalidClaim));
        }
    }

    [Test]
    public void Verify_Secp256k1SignedByAnotherKey_ReturnsFalse()
    {
        Transaction tx = CreateFrameTx(sender: TestItem.PrivateKeyB.Address);
        SignSecp256k1(tx, TestItem.PrivateKeyC, null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Verify(tx, out string? error), Is.False);
            Assert.That(error, Is.EqualTo(FrameTxClaimVerifier.InvalidClaim));
        }
    }

    // v is the 0/1 recovery id and s must be low, so every signature has exactly one witness.
    [TestCase(false, TestName = "Verify_Secp256k1WithLegacy27RecoveryId_ReturnsFalse")]
    [TestCase(true, TestName = "Verify_Secp256k1WithHighS_ReturnsFalse")]
    public void Verify_Secp256k1NonCanonicalWitness_ReturnsFalse(bool highS)
    {
        Transaction tx = CreateFrameTx(sender: TestItem.PrivateKeyB.Address);
        SignSecp256k1(tx, TestItem.PrivateKeyB, null);
        byte[] witness = tx.ClaimWitnesses![0];
        if (highS)
        {
            UInt256 s = new(witness.AsSpan(33, 32), isBigEndian: true);
            (SecP256k1Curve.N - s).ToBigEndian(witness.AsSpan(33, 32));
            witness[0] ^= 1;
        }
        else
        {
            witness[0] += 27;
        }

        Assert.That(Verify(tx, out _), Is.False);
    }

    [Test]
    public void Verify_Secp256k1ClaimOverAnExplicitDigest_ReturnsTrueOnlyForThatDigest()
    {
        Transaction tx = CreateFrameTx();
        (TxFrameClaim claim, byte[] witness) = FrameTxTestFrames.DigestClaim(TestItem.PrivateKeyC, 0x07);
        tx.FrameClaims = [claim];
        tx.ClaimWitnesses = [witness];
        Transaction otherDigest = CreateFrameTx();
        otherDigest.FrameClaims = [FrameTxTestFrames.DigestClaim(TestItem.PrivateKeyC, 0x08).Claim];
        otherDigest.ClaimWitnesses = [witness];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Verify(tx, out string? error), Is.True, error);
            Assert.That(Verify(otherDigest, out _), Is.False);
        }
    }

    [TestCase(0, FrameTxClaimVerifier.WitnessCountMismatch, TestName = "CheckWitnesses_FewerWitnessesThanClaims_Rejected")]
    [TestCase(64, FrameTxClaimVerifier.InvalidWitnessLength, TestName = "CheckWitnesses_ProtocolWitnessOfTheWrongLength_Rejected")]
    public void CheckWitnesses_ShapeMismatch_Rejected(int witnessLength, string expected)
    {
        Transaction tx = CreateFrameTx();
        tx.FrameClaims = [FrameTxTestFrames.Secp256k1Claim(null)];
        tx.ClaimWitnesses = witnessLength == 0 ? [] : [new byte[witnessLength]];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(FrameTxClaimVerifier.CheckWitnesses(tx, allowPlaceholders: false, out string? error), Is.False);
            Assert.That(error, Is.EqualTo(expected));
        }
    }

    [Test]
    public void CheckWitnesses_CustomClaim_TakesItsDeclaredLength()
    {
        Transaction tx = CreateFrameTx();
        tx.FrameClaims = [new TxFrameClaim(TestItem.AddressD, default, default, 1_000, 3)];
        tx.ClaimWitnesses = [[1, 2, 3]];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(FrameTxClaimVerifier.CheckWitnesses(tx, allowPlaceholders: false, out string? error), Is.True, error);
            tx.ClaimWitnesses = [[1, 2]];
            Assert.That(FrameTxClaimVerifier.CheckWitnesses(tx, allowPlaceholders: false, out error), Is.False);
            Assert.That(error, Is.EqualTo(FrameTxClaimVerifier.InvalidWitnessLength));
        }
    }

    [TestCase(false, false, TestName = "CheckWitnesses_MissingList_Rejected")]
    [TestCase(true, true, TestName = "CheckWitnesses_EmptyPlaceholderWhenAllowed_Accepted")]
    public void CheckWitnesses_MissingWitnesses(bool placeholder, bool expected)
    {
        Transaction tx = CreateFrameTx();
        tx.FrameClaims = [FrameTxTestFrames.Secp256k1Claim(null)];
        tx.ClaimWitnesses = placeholder ? [[]] : null;

        Assert.That(FrameTxClaimVerifier.CheckWitnesses(tx, allowPlaceholders: placeholder, out _), Is.EqualTo(expected));
    }

    [Test]
    public void Verify_P256ValidSignature_ReturnsTrue()
    {
        Transaction tx = CreateFrameTx();
        SignP256(tx);

        Assert.That(Verify(tx, out string? error), Is.True, error);
    }

    [Test]
    public void Verify_P256KeyNotTheAddressOfThePublicKey_ReturnsFalse()
    {
        Transaction tx = CreateFrameTx();
        SignP256(tx);
        tx.FrameClaims = [new TxFrameClaim(Eip8141Constants.P256VerifierAddress, TestItem.AddressC.Bytes.ToArray(), default, 0, 0)];

        Assert.That(Verify(tx, out _), Is.False);
    }

    [Test]
    public void Verify_P256WithHighS_ReturnsFalse()
    {
        // P256VERIFY accepts both s and N - s, so the low-s gate must reject the high-s encoding.
        Transaction tx = CreateFrameTx();
        byte[] witness = SignP256(tx);

        UInt256 lowS = new(witness.AsSpan(32, 32), isBigEndian: true);
        (SecP256r1Curve.N - lowS).ToBigEndian(witness.AsSpan(32, 32));

        Assert.That(Verify(tx, out _), Is.False);
    }

    [Test]
    public void Verify_P256WithoutPrecompile_RejectedAsNotSupported()
    {
        Transaction tx = CreateFrameTx();
        SignP256(tx);

        bool verified = FrameTxClaimVerifier.VerifyProtocolClaims(tx, _ethereumEcdsa, p256Precompile: null, UnavailableLeanSphincsVerifier.Instance, _spec,
            preempt: null, out _, out _, out string? error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(verified, Is.False);
            Assert.That(error, Is.EqualTo(FrameTxClaimVerifier.P256NotSupported));
        }
    }

    [Test]
    public void Verify_LeanSphincsClaim_DelegatesToTheVerifier([Values] bool valid)
    {
        Transaction tx = CreateFrameTx();
        byte[] publicKey = Fill(32, 0x42);
        tx.FrameClaims = [new TxFrameClaim(Eip8141Constants.LeanSphincsVerifierAddress, publicKey, default, 0, 0)];
        tx.ClaimWitnesses = [new byte[Eip8141Constants.LeanSphincsSignatureSize]];
        RecordingLeanSphincsVerifier leanSphincs = new(valid);

        bool verified = FrameTxClaimVerifier.VerifyProtocolClaims(tx, _ethereumEcdsa, SecP256r1Precompile.Instance, leanSphincs, _spec,
            preempt: null, out _, out bool skipped, out _);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(verified, Is.EqualTo(valid));
            Assert.That(skipped, Is.False);
            Assert.That(leanSphincs.PublicKey, Is.EqualTo(new ValueHash256(publicKey)));
            Assert.That(leanSphincs.Message, Is.EqualTo(FrameTxSigHash.ComputeValue(tx)));
            Assert.That(leanSphincs.SignatureLength, Is.EqualTo(Eip8141Constants.LeanSphincsSignatureSize));
        }
    }

    [Test]
    public void Verify_WithoutALeanSphincsImplementation_RefusesTheClaim()
    {
        Transaction tx = CreateFrameTx();
        tx.FrameClaims = [new TxFrameClaim(Eip8141Constants.LeanSphincsVerifierAddress, Fill(32, 0x42), default, 0, 0)];
        tx.ClaimWitnesses = [new byte[Eip8141Constants.LeanSphincsSignatureSize]];

        bool verified = Verify(tx, out string? error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(verified, Is.False);
            Assert.That(error, Is.EqualTo(FrameTxClaimVerifier.LeanSphincsUnavailable));
        }
    }

    // The pool leaves to simulation what needs state, or a verifier it does not hold.
    [TestCase(true, TestName = "VerifyProtocolClaims_CustomClaim_IsSkipped")]
    [TestCase(false, TestName = "VerifyProtocolClaims_LeanSphincsClaimWithoutAVerifier_IsSkipped")]
    public void VerifyProtocolClaims_ClaimItCannotVerify_IsSkipped(bool custom)
    {
        Transaction tx = CreateFrameTx();
        tx.FrameClaims = custom
            ? [new TxFrameClaim(TestItem.AddressD, default, default, 1_000, 0)]
            : [new TxFrameClaim(Eip8141Constants.LeanSphincsVerifierAddress, Fill(32, 0x42), default, 0, 0)];
        tx.ClaimWitnesses = [custom ? [] : new byte[Eip8141Constants.LeanSphincsSignatureSize]];

        bool verified = FrameTxClaimVerifier.VerifyProtocolClaims(tx, _ethereumEcdsa, SecP256r1Precompile.Instance, leanSphincs: null, _spec,
            preempt: null, out _, out bool skipped, out _);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(verified, Is.True);
            Assert.That(skipped, Is.True);
        }
    }

    [Test]
    public void VerifyProtocolClaims_Preempted_StopsBeforeTheSecondVerificationWithoutAVerdict()
    {
        Transaction tx = CreateFrameTx();
        (TxFrameClaim first, byte[] firstWitness) = FrameTxTestFrames.DigestClaim(TestItem.PrivateKeyB, 1);
        (TxFrameClaim second, byte[] secondWitness) = FrameTxTestFrames.DigestClaim(TestItem.PrivateKeyB, 2);
        tx.FrameClaims = [first, second];
        tx.ClaimWitnesses = [firstWitness, secondWitness];
        int polls = 0;

        bool verified = FrameTxClaimVerifier.VerifyProtocolClaims(tx, _ethereumEcdsa, SecP256r1Precompile.Instance, null, _spec,
            () => ++polls > 0, out bool preempted, out _, out string? error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(verified, Is.False);
            Assert.That(preempted, Is.True);
            Assert.That(error, Is.Null);
            Assert.That(polls, Is.EqualTo(1), "a single verification is never preempted");
        }
    }

    private bool Verify(Transaction tx, out string? error) =>
        FrameTxClaimVerifier.CheckWitnesses(tx, allowPlaceholders: false, out error)
        && FrameTxClaimVerifier.VerifyProtocolClaims(tx, _ethereumEcdsa, SecP256r1Precompile.Instance, UnavailableLeanSphincsVerifier.Instance, _spec,
            preempt: null, out _, out _, out error);

    private void SignSecp256k1(Transaction tx, PrivateKey key, Address? signer)
    {
        tx.FrameClaims = [FrameTxTestFrames.Secp256k1Claim(signer)];
        tx.ClaimWitnesses = [FrameTxTestFrames.Secp256k1SignatureBytes(_ecdsa.Sign(key, FrameTxSigHash.ComputeValue(tx)))];
    }

    /// <summary>Signs <paramref name="tx"/> with a fresh P-256 key and installs the low-s claim it produces,
    /// returning the witness so a caller can corrupt it in place.</summary>
    private static byte[] SignP256(Transaction tx)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        ECParameters pub = key.ExportParameters(includePrivateParameters: false);
        byte[] qx = Pad32(pub.Q.X!);
        byte[] qy = Pad32(pub.Q.Y!);
        byte[] signer = Keccak.Compute([.. qx, .. qy]).Bytes[12..].ToArray();

        tx.FrameClaims = [new TxFrameClaim(Eip8141Constants.P256VerifierAddress, signer, default, 0, 0)];
        ValueHash256 sigHash = FrameTxSigHash.ComputeValue(tx);

        byte[] witness = new byte[Eip8141Constants.P256WitnessLength];
        key.SignHash(sigHash.Bytes).CopyTo(witness.AsSpan(0)); // IEEE P1363: r || s
        qx.CopyTo(witness.AsSpan(64));
        qy.CopyTo(witness.AsSpan(96));
        // .NET does not guarantee low s; the spec requires it, so normalize before verifying.
        UInt256 s = new(witness.AsSpan(32, 32), isBigEndian: true);
        if (s > SecP256r1Curve.HalfN)
        {
            (SecP256r1Curve.N - s).ToBigEndian(witness.AsSpan(32, 32));
        }

        tx.ClaimWitnesses = [witness];
        return witness;
    }

    private static byte[] BlobVersionedHash(byte fill)
    {
        byte[] hash = Fill(Hash256.Size, fill);
        hash[0] = 0x01;
        return hash;
    }

    private static byte[] Fill(int length, byte fill)
    {
        byte[] bytes = new byte[length];
        bytes.AsSpan().Fill(fill);
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
            Frames = [new TxFrame(FrameMode.Verify, FrameFlags.ApproveExecutionAndPayment, null, 100_000, default, default)],
            FrameClaims = [],
            GasPrice = 1,
            MaxFee = 100,
        };

    /// <summary>Records what it was asked to verify; NSubstitute cannot match a span argument.</summary>
    private sealed class RecordingLeanSphincsVerifier(bool result) : ILeanSphincsVerifier
    {
        public ValueHash256 PublicKey { get; private set; }
        public ValueHash256 Message { get; private set; }
        public int SignatureLength { get; private set; }

        public bool Verify(in ValueHash256 publicKey, in ValueHash256 message, ReadOnlySpan<byte> signature)
        {
            PublicKey = publicKey;
            Message = message;
            SignatureLength = signature.Length;
            return result;
        }
    }
}
