// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using static Nethermind.BeaconChain.Test.StateTransition.GloasTestFixtures;
using G1Affine = Nethermind.Crypto.Bls.P1Affine;

namespace Nethermind.BeaconChain.Test.Crypto;

[TestFixture]
public class BlockSignatureBatchTests
{
    private static readonly Hash256 Message = Hash(0x5A);

    // ethereum/bls12-381-tests v0.1.2 deserialization_fails_not_in_G1 / _not_in_G2: on-curve points outside the prime-order subgroups.
    private static readonly byte[] NotInG1Pubkey = Bytes.FromHexString("0x8123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");
    private static readonly byte[] NotInG2Signature = Bytes.FromHexString("0x8123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");

    private static readonly byte[] G1Infinity = G1PointAtInfinity();

    private static G1Affine PublicKey(int keyIndex) => new Bls.P1(DeriveKey(keyIndex)).ToAffine();

    private static G1Affine DecodePublicKey(byte[] compressed)
    {
        G1Affine publicKey = new(new long[G1Affine.Sz]);
        Assert.That(publicKey.TryDecode(compressed, out _), Is.True, "fixture bug: the public key must decode");
        return publicKey;
    }

    private static BlsSignature SignedBy(int keyIndex, Hash256 message) => Sign(DeriveKey(keyIndex), message);

    [Test]
    public void Signature_outside_G2_is_rejected([Values] bool deferred)
    {
        G1Affine publicKey = PublicKey(3);
        BlsSignature validSignature = SignedBy(3, Message);
        Assert.That(BlsSigner.Verify(publicKey, validSignature.Bytes, Message.Bytes), Is.True);
        BlsSignature signature = OffSubgroupKeys.WithG2Torsion(validSignature);
        Bls.P2 point = new(new long[Bls.P2.Sz]);
        Assert.That(point.TryDecode(signature.Bytes, out _), Is.True);
        Assert.That(point.OnCurve(), Is.True);
        Assert.That(point.ToAffine().InGroup(), Is.False);
        bool pairingOnly = BlsSigner.Verify(publicKey, new BlsSigner.Signature(point), Message.Bytes);
        TestContext.Out.WriteLine($"S + T pairing-only verification: {pairingOnly}");

        BlockSignatureBatch batch = new();
        bool accepted = BlockSignatureBatch.Verify(publicKey, signature, Message, deferred ? batch.Defer("outside G2") : null);

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(accepted, Is.False, $"S + T pairing-only verification: {pairingOnly}");
        Assert.That(batch.Count, Is.Zero);
    }

    // IETF BLS CoreVerify 2.7: reject signatures shifted by every 13/23-order twist point. Pairing already rejects these; TryCreate still must refuse deferral.
    [TestCase(13, 168)]
    [TestCase(23, 528)]
    public void Signatures_with_small_order_torsion_are_refused(int prime, int points)
    {
        G1Affine publicKey = PublicKey(3);
        BlsSignature validSignature = SignedBy(3, Message);
        IReadOnlyCollection<byte[]> torsion = OffSubgroupKeys.G2TorsionOfOrder(prime);
        Assert.That(torsion, Has.Count.EqualTo(points), "fixture: the whole torsion of that order");
        int pairingAccepted = 0;
        int serialAccepted = 0;
        int deferredAccepted = 0;
        BlockSignatureBatch batch = new();
        foreach (byte[] encoded in torsion)
        {
            Bls.P2Affine t = new(new long[Bls.P2Affine.Sz]);
            Assert.That(t.TryDecode(encoded, out _), Is.True);
            Bls.P2 point = new(new long[Bls.P2.Sz]);
            point.Decode(validSignature.Bytes);
            point.Add(t);
            Assert.That(point.ToAffine().InGroup(), Is.False);
            BlsSignature signature = new(point.Compress());
            if (BlsSigner.Verify(publicKey, new BlsSigner.Signature(point), Message.Bytes)) pairingAccepted++;
            if (BlockSignatureBatch.Verify(publicKey, signature, Message, null)) serialAccepted++;
            if (BlockSignatureBatch.Verify(publicKey, signature, Message, batch.Defer("outside G2"))) deferredAccepted++;
        }

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(pairingAccepted, Is.Zero, "the pairing alone");
        Assert.That(serialAccepted, Is.Zero);
        Assert.That(deferredAccepted, Is.Zero);
        Assert.That(batch.Count, Is.Zero);
    }

    private static IEnumerable<TestCaseData> PointsOutsideTheBatchConditions()
    {
        yield return new TestCaseData(G1Infinity, SignatureSets.G2PointAtInfinity, false).SetName("infinity_public_key_with_infinity_signature");
        yield return new TestCaseData(G1Infinity, SignedBy(3, Message).Bytes.ToArray(), false).SetName("infinity_public_key_with_a_real_signature");
        yield return new TestCaseData(NotInG1Pubkey, SignedBy(3, Message).Bytes.ToArray(), false).SetName("public_key_outside_g1");
        yield return new TestCaseData(new Bls.P1(DeriveKey(3)).Compress(), NotInG2Signature, false).SetName("signature_outside_g2");
        yield return new TestCaseData(new Bls.P1(DeriveKey(1)).Compress(), Bytes.FromHexString("0x" + new string('f', 2 * BlsSignature.Length)), false)
            .SetName("A_signature_the_serial_path_cannot_decode_is_refused_at_its_call_site");
    }

    // Undecodable and off-subgroup sets must get the serial verdict immediately; batching is only sound over subgroup points.
    [TestCaseSource(nameof(PointsOutsideTheBatchConditions))]
    public void A_set_outside_the_batch_conditions_gets_the_serial_verdict_at_once(byte[] compressedPublicKey, byte[] signature, bool serialVerdict)
    {
        G1Affine publicKey = DecodePublicKey(compressedPublicKey);
        BlsSignature blsSignature = new(signature);
        BlockSignatureBatch batch = new();

        bool serial = BlockSignatureBatch.Verify(publicKey, blsSignature, Message, deferral: null);
        bool deferred = BlockSignatureBatch.Verify(publicKey, blsSignature, Message, batch.Defer("outside"));

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(serial, Is.EqualTo(serialVerdict), "fixture bug: the serial verdict this case pins");
        Assert.That(deferred, Is.EqualTo(serial));
        Assert.That(batch.Count, Is.Zero, "the set must not be deferred");
        Assert.That(batch.Verify, Throws.Nothing);
    }

    [Test]
    public void Valid_signatures_are_deferred_and_verify()
    {
        BlockSignatureBatch batch = new();
        for (int i = 0; i < 4; i++)
        {
            Hash256 message = Hash((byte)(0x60 + i));
            Assert.That(BlockSignatureBatch.Verify(PublicKey(i), SignedBy(i, message), message, batch.Defer($"set {i}")), Is.True);
        }

        Assert.That(batch.Count, Is.EqualTo(4));
        Assert.That(batch.ThrowFirstInvalid, Throws.Nothing);
        Assert.That(batch.Verify, Throws.Nothing);
        Assert.That(batch.Count, Is.Zero, "a verified batch is emptied");
    }

    [TestCase(0)]
    [TestCase(2)]
    [TestCase(4)]
    public void One_invalid_signature_anywhere_is_refused_with_its_own_message(int invalidPosition)
    {
        BlockSignatureBatch batch = BatchWithInvalidAt(5, invalidPosition, alsoInvalid: -1);

        Assert.That(batch.Verify, Throws.TypeOf<BeaconStateException>().With.Message.EqualTo($"set {invalidPosition}"));
    }

    [Test]
    public void ThrowFirstInvalid_names_the_earliest_invalid_signature()
    {
        BlockSignatureBatch batch = BatchWithInvalidAt(5, 1, 3);

        Assert.That(batch.ThrowFirstInvalid, Throws.TypeOf<BeaconStateException>().With.Message.EqualTo("set 1"));
    }

    private static BlockSignatureBatch BatchWithInvalidAt(int count, int invalid, int alsoInvalid)
    {
        BlockSignatureBatch batch = new();
        for (int i = 0; i < count; i++)
        {
            Hash256 message = Hash((byte)(0x70 + i));
            // Signed by another key: decodes and subgroup-checks, invalid only by the pairing.
            int signer = i == invalid || i == alsoInvalid ? i + 100 : i;
            Assert.That(BlockSignatureBatch.Verify(PublicKey(i), SignedBy(signer, message), message, batch.Defer($"set {i}")), Is.True, "every set must be deferred");
        }

        return batch;
    }
}
