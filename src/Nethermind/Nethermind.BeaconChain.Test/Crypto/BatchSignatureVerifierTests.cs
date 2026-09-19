// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.BeaconChain.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using NUnit.Framework;

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

    /// <summary>
    /// Signs with <paramref name="signerKeyIndex"/>'s real key over a real message, but claims a
    /// different, unrelated public key. Both points decode and subgroup-check cleanly (they are
    /// genuine keys), so this is invalid purely because the pairing equation does not hold -
    /// distinct from a malformed-encoding rejection.
    /// </summary>
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

        Assert.Multiple(() =>
        {
            Assert.That(BatchSignatureVerifier.VerifyBatch(sets), Is.True);
            Assert.That(BatchSignatureVerifier.FindInvalid(sets), Is.EqualTo(-1));
        });
    }

    [TestCase(0)]
    [TestCase(2)]
    [TestCase(4)]
    public void Single_bad_signature_fails_batch_regardless_of_position(int badPosition)
    {
        List<BlsSignatureSet> sets = [.. Enumerable.Range(0, 5).Select(i => MakeSet(i, Msg((byte)(i + 10))))];
        sets[badPosition] = MakeMismatchedSet(badPosition, Msg((byte)(badPosition + 10)), badPosition + 100);

        Assert.Multiple(() =>
        {
            Assert.That(BatchSignatureVerifier.VerifyBatch(sets), Is.False, $"batch must reject with the bad set at position {badPosition}");
            Assert.That(BatchSignatureVerifier.FindInvalid(sets), Is.EqualTo(badPosition));
        });
    }

    [Test]
    public void Cancellation_attack_defeated_by_randomization()
    {
        // Two independent keys sign the SAME message, then the (pubkey, signature) pairing is
        // swapped: set1 claims sk1's real signature belongs to pk2, set2 claims sk2's real
        // signature belongs to pk1. Each set alone is an invalid signature, but their unrandomized
        // pairing terms cancel exactly:
        //   e(H(m),pk2)*e(sig1,-G) * e(H(m),pk1)*e(sig2,-G) = e(H(m),G)^((sk2-sk1)+(sk1-sk2)) = 1
        // for ANY sk1 != sk2 - this is the classic attack an unrandomized batch cannot detect.
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

        // Prove this is a genuine cancellation and not just "a wrong signature fails": the naive,
        // unrandomized accumulation (an implicit scalar of 1 for every set) must actually accept.
        Bls.Pairing naive = new(hashOrEncode: true, BatchSignatureVerifier.Cryptosuite);
        naive.Aggregate(set1!.PublicKey, set1.Signature, set1.Message);
        naive.Aggregate(set2!.PublicKey, set2.Signature, set2.Message);
        naive.Commit();
        Assert.That(naive.FinalVerify(), Is.True, "the crafted pair must cancel exactly when unrandomized");

        Assert.Multiple(() =>
        {
            Assert.That(BatchSignatureVerifier.VerifyBatch(sets), Is.False, "randomization must defeat the cancellation");
            Assert.That(BatchSignatureVerifier.FindInvalid(sets), Is.EqualTo(0));
        });
    }

    [Test]
    public void Infinity_public_key_rejected()
    {
        byte[] message = Msg(0x11);
        byte[] sig = Sign(DeriveKey(3), message);

        byte[] infinityPubkey = new byte[48];
        infinityPubkey[0] = 0xc0; // compressed-point flag + infinity flag, zero body: the canonical G1 infinity encoding

        Assert.That(BlsSignatureSet.TryCreate(infinityPubkey, message, sig, out BlsSignatureSet? set), Is.False);
        Assert.That(set, Is.Null);
    }

    // Choice: an empty batch has no constraint to violate, so it verifies true - the same
    // vacuous-truth convention a serial loop over zero sets (all() of an empty sequence) gives.
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
