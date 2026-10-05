// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Test.StateTransition;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;

namespace Nethermind.BeaconChain.Test.Crypto;

[TestFixture]
public class BatchSignatureVerifierTests
{
    private static readonly byte[] MasterSkBytes = Bytes.FromHexString("0x2cd4ba406b522459d57a0bed51a397435c0bb11dd5f3ca1152b3694bb91d7c22");

    private static Bls.SecretKey DeriveKey(int index) =>
        new(new Bls.SecretKey(MasterSkBytes, Bls.ByteOrder.LittleEndian), unchecked((uint)index));

    private static byte[] CompressedPubkey(Bls.SecretKey sk) => new Bls.P1(sk).Compress();
    private static byte[] Sign(Bls.SecretKey sk, byte[] message) => BlsSigner.Sign(sk, message).Bytes.ToArray();
    private static byte[] Msg(byte fill) => Enumerable.Repeat(fill, 32).ToArray();

    private static BlsSignatureSet MakeSet(int keyIndex, byte[] message)
    {
        Bls.SecretKey sk = DeriveKey(keyIndex);
        bool ok = BlsSignatureSet.TryCreate(CompressedPubkey(sk), message, Sign(sk, message), out BlsSignatureSet? set);
        Assert.That(ok, Is.True, "fixture signature must itself be valid");
        return set!;
    }

    // Both keys are valid subgroup points; only the mismatched pairing makes this signature invalid.
    private static BlsSignatureSet MakeMismatchedSet(int signerKeyIndex, byte[] message, int claimedKeyIndex)
    {
        byte[] sig = Sign(DeriveKey(signerKeyIndex), message);
        byte[] claimedPk = CompressedPubkey(DeriveKey(claimedKeyIndex));
        bool ok = BlsSignatureSet.TryCreate(claimedPk, message, sig, out BlsSignatureSet? set);
        Assert.That(ok, Is.True, "mismatched fixture must still decode - it is invalid only by content");
        return set!;
    }

    [Test]
    public void Agreement_batch_and_serial_both_accept_valid_sets()
    {
        List<BlsSignatureSet> sets = [.. Enumerable.Range(0, 6).Select(i => MakeSet(i, Msg((byte)i)))];

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(BatchSignatureVerifier.VerifyBatch(sets), Is.True);
        Assert.That(BatchSignatureVerifier.FindInvalid(sets), Is.EqualTo(-1));
    }

    [TestCase(0)]
    [TestCase(2)]
    [TestCase(4)]
    public void Single_bad_signature_fails_batch_regardless_of_position(int badPosition)
    {
        List<BlsSignatureSet> sets = [.. Enumerable.Range(0, 5).Select(i => MakeSet(i, Msg((byte)(i + 10))))];
        sets[badPosition] = MakeMismatchedSet(badPosition, Msg((byte)(badPosition + 10)), badPosition + 100);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(BatchSignatureVerifier.VerifyBatch(sets), Is.False, $"batch must reject with the bad set at position {badPosition}");
        Assert.That(BatchSignatureVerifier.FindInvalid(sets), Is.EqualTo(badPosition));
    }

    [Test]
    public void Cancellation_attack_defeated_by_randomization()
    {
        // Swapped same-message signatures cancel in an unrandomized batch: exponents (sk2-sk1)+(sk1-sk2)=0.
        byte[] message = Msg(0xAB);
        Bls.SecretKey sk1 = DeriveKey(1);
        Bls.SecretKey sk2 = DeriveKey(2);
        byte[] pk1 = CompressedPubkey(sk1);
        byte[] pk2 = CompressedPubkey(sk2);
        byte[] sig1 = Sign(sk1, message);
        byte[] sig2 = Sign(sk2, message);

        Assert.That(BlsSignatureSet.TryCreate(pk2, message, sig1, out BlsSignatureSet? set1), Is.True);
        Assert.That(BlsSignatureSet.TryCreate(pk1, message, sig2, out BlsSignatureSet? set2), Is.True);
        List<BlsSignatureSet> sets = [set1!, set2!];


        Bls.Pairing naive = new(hashOrEncode: true, BatchSignatureVerifier.Cryptosuite);
        naive.Aggregate(set1!.PublicKey, set1.Signature, set1.Message);
        naive.Aggregate(set2!.PublicKey, set2.Signature, set2.Message);
        naive.Commit();
        Assert.That(naive.FinalVerify(), Is.True, "the crafted pair must cancel exactly when unrandomized");

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(BatchSignatureVerifier.VerifyBatch(sets), Is.False, "randomization must defeat the cancellation");
        Assert.That(BatchSignatureVerifier.FindInvalid(sets), Is.EqualTo(0));
    }

    [Test]
    public void Infinity_public_key_rejected()
    {
        byte[] message = Msg(0x11);
        byte[] sig = Sign(DeriveKey(3), message);

        byte[] infinityPubkey = GloasTestFixtures.G1PointAtInfinity();

        Assert.That(BlsSignatureSet.TryCreate(infinityPubkey, message, sig, out BlsSignatureSet? set), Is.False);
        Assert.That(set, Is.Null);
    }

    // ethereum/bls12-381-tests v0.1.2, deserialization_G1/deserialization_fails_not_in_G1 and
    // deserialization_G2/deserialization_fails_not_in_G2: x-coordinates that decode to an on-curve point
    // outside the prime-order subgroup, so only the InGroup check can reject them.
    private static readonly byte[] NotInG1Pubkey = Bytes.FromHexString("0x8123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");
    private static readonly byte[] NotInG2Signature = Bytes.FromHexString("0x8123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");

    [Test]
    public void Off_subgroup_public_key_rejected()
    {
        Bls.P1Affine point = new(new long[Bls.P1Affine.Sz]);
        bool decodes = point.TryDecode(NotInG1Pubkey, out _);
        bool isInfinity = point.IsInf();
        bool inGroup = point.InGroup();
        Assert.Multiple(() =>
        {
            Assert.That(decodes, Is.True, "the vector must decode, or this pins the encoding branch instead of the subgroup branch");
            Assert.That(isInfinity, Is.False, "the vector must not be infinity, or this pins the infinity branch instead of the subgroup branch");
            Assert.That(inGroup, Is.False, "the vector must lie outside G1");
        });

        byte[] message = Msg(0x22);
        byte[] sig = Sign(DeriveKey(3), message);

        Assert.That(BlsSignatureSet.TryCreate(NotInG1Pubkey, message, sig, out BlsSignatureSet? set), Is.False);
        Assert.That(set, Is.Null);
    }

    [Test]
    public void Off_subgroup_signature_rejected()
    {
        Bls.P2Affine point = new(new long[Bls.P2Affine.Sz]);
        bool decodes = point.TryDecode(NotInG2Signature, out _);
        bool inGroup = point.InGroup();
        Assert.Multiple(() =>
        {
            Assert.That(decodes, Is.True, "the vector must decode, or this pins the encoding branch instead of the subgroup branch");
            Assert.That(inGroup, Is.False, "the vector must lie outside G2");
        });

        byte[] message = Msg(0x33);
        byte[] pk = CompressedPubkey(DeriveKey(3));

        Assert.That(BlsSignatureSet.TryCreate(pk, message, NotInG2Signature, out BlsSignatureSet? set), Is.False);
        Assert.That(set, Is.Null);
    }


    [Test]
    public void Empty_batch_verifies_true() =>
        Assert.Multiple(() =>
        {
            Assert.That(BatchSignatureVerifier.VerifyBatch([]), Is.True);
            Assert.That(BatchSignatureVerifier.FindInvalid([]), Is.EqualTo(-1));
        });

    [Test]
    public void Never_a_false_accept_over_randomized_inputs()
    {
        // Fixed seed: any failure here reproduces exactly. The property under test is one-sided -
        // batch is allowed to (and by design does not) diverge from serial when serial accepts,
        // but batch accepting what serial rejects would be the actual security failure.
        Random random = new(20260919);
        const int trials = 200;
        const int maxSetsPerTrial = 6;

        for (int trial = 0; trial < trials; trial++)
        {
            int count = random.Next(1, maxSetsPerTrial + 1);
            List<BlsSignatureSet> sets = new(count);
            for (int i = 0; i < count; i++)
            {
                int keyIndex = trial * maxSetsPerTrial + i;
                byte[] message = Msg((byte)(keyIndex % 256));
                bool valid = random.Next(2) == 0;
                sets.Add(valid
                    ? MakeSet(keyIndex, message)
                    : MakeMismatchedSet(keyIndex, message, keyIndex + 10_000));
            }

            bool serialAllValid = BatchSignatureVerifier.FindInvalid(sets) < 0;
            bool batchAccepted = BatchSignatureVerifier.VerifyBatch(sets);

            Assert.That(batchAccepted && !serialAllValid, Is.False,
                $"trial {trial}: batch accepted a set serial verification rejected (count={count})");
            if (serialAllValid)
                Assert.That(batchAccepted, Is.True, $"trial {trial}: batch rejected an all-valid set (count={count})");
        }
    }
}
