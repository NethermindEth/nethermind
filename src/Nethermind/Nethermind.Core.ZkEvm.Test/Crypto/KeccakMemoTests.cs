// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.Core.ZkEvm.Test.Crypto;

/// <summary>
/// The guest's direct-mapped keccak memo in <c>KeccakCache.zkevm.cs</c>, which the host suite cannot
/// reach: <c>Nethermind.Core.Test</c> builds the seqlock cache of <c>KeccakCache.std.cs</c> instead, so
/// every case here would exercise the other implementation there.
/// </summary>
/// <remarks>
/// Driven through the memo's probe and store rather than <see cref="KeccakCache.Compute"/>, because in a
/// ZK_EVM build the permutation is a zkVM precompile and calling it outside the guest throws. A stand-in
/// digest serves just as well: what the memo owes its caller is the digest stored for <em>that</em>
/// input, whatever the digest happens to be.
/// </remarks>
public class KeccakMemoTests
{
    private const int MinLength = (int)KeccakCache.MinMemoLength;
    private const int MaxLength = (int)KeccakCache.MaxMemoLength;

    /// <summary>Slots in the memo, so the tests can size themselves against replacement.</summary>
    private const int SlotCount = 1 << KeccakCache.MemoSlotBits;

    [Test]
    public void Memo_answers_with_the_digest_stored_for_an_input([Range(MinLength, MaxLength)] int length)
    {
        byte[] input = Pattern(length, seed: 3);
        ValueHash256 digest = Digest(length);

        Assert.That(KeccakCache.TryReadMemo(input, out _), Is.False, "no digest stored for this input yet");

        KeccakCache.WriteMemo(input, digest);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(KeccakCache.TryReadMemo(input, out ValueHash256 read), Is.True);
            Assert.That(read, Is.EqualTo(digest));
        }
    }

    public static IEnumerable<TestCaseData> PadAlikePairs()
    {
        foreach ((int shorter, int longer) in new[] { (8, 16), (9, 16), (20, 32), (31, 32), (63, 64) })
        {
            yield return new TestCaseData(shorter, longer).SetName(
                $"Memo_separates_{shorter}_bytes_from_the_same_bytes_padded_to_{longer}");
        }
    }

    /// <remarks>
    /// An input and its zero-extension pad to the same key words - a 20-byte address against a 32-byte
    /// topic ending in twelve zero bytes is the shape a contract can pick deliberately - so only the
    /// length keeps the two apart, in the slot index and in the slot's length word. Both being readable
    /// at once is also what says the length reached the index.
    /// </remarks>
    [TestCaseSource(nameof(PadAlikePairs))]
    public void Memo_separates_an_input_from_its_zero_padded_form(int shorter, int longer)
    {
        byte[] unpadded = Pattern(shorter, seed: 11);
        byte[] padded = new byte[longer];
        unpadded.CopyTo(padded, 0);

        ValueHash256 unpaddedDigest = Digest(shorter);
        ValueHash256 paddedDigest = Digest(longer + MaxLength);

        KeccakCache.WriteMemo(padded, paddedDigest);
        KeccakCache.WriteMemo(unpadded, unpaddedDigest);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(KeccakCache.TryReadMemo(unpadded, out ValueHash256 readUnpadded), Is.True);
            Assert.That(readUnpadded, Is.EqualTo(unpaddedDigest));
            Assert.That(KeccakCache.TryReadMemo(padded, out ValueHash256 readPadded), Is.True);
            Assert.That(readPadded, Is.EqualTo(paddedDigest));
        }
    }

    /// <remarks>
    /// Exchanging two whole words leaves the XOR-folded slot index unchanged, so this pair collides by
    /// construction. The displaced input reading as a miss is then both the replacement under test and
    /// the construction still holding - if the index stops folding the words that way, the pair no
    /// longer shares a slot and this fails rather than passing vacuously.
    /// </remarks>
    [Test]
    public void Memo_replaces_a_key_colliding_in_its_slot([Values(16, 20, 24, 32, 64)] int length)
    {
        byte[] input = Pattern(length, seed: 23);
        byte[] colliding = (byte[])input.Clone();
        for (int i = 0; i < sizeof(ulong); i++)
        {
            (colliding[i], colliding[sizeof(ulong) + i]) = (colliding[sizeof(ulong) + i], colliding[i]);
        }

        ValueHash256 digest = Digest(length);
        ValueHash256 collidingDigest = Digest(length + MaxLength);

        KeccakCache.WriteMemo(input, digest);
        KeccakCache.WriteMemo(colliding, collidingDigest);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(KeccakCache.TryReadMemo(colliding, out ValueHash256 read), Is.True);
            Assert.That(read, Is.EqualTo(collidingDigest));
            Assert.That(KeccakCache.TryReadMemo(input, out _), Is.False);
        }
    }

    /// <remarks>
    /// A same-slot pair whose whole words are identical and whose partial tails differ, so the tail word
    /// the store keeps past the last whole word is the only thing separating them: without that compare
    /// the second input is handed the first one's digest.
    /// </remarks>
    [Test]
    public void Memo_rejects_a_same_slot_key_differing_only_in_its_partial_word()
    {
        const int length = 20;

        AssertOnlyTheSlotHolderIsAnswered(tail =>
        {
            byte[] holder = Pattern(length, seed: 41);
            byte[] partner = (byte[])holder.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(partner.AsSpan(length & ~7), tail);
            return (holder, partner);
        });
    }

    /// <remarks>
    /// A same-slot pair where one input is the other's prefix, which is what the slot's length word is
    /// for: the shorter probe compares only the words it has, and the longer input stored all of them,
    /// so without the length compare it is handed the longer input's digest.
    /// </remarks>
    [Test]
    public void Memo_rejects_a_same_slot_key_that_prefixes_the_one_it_holds()
    {
        const int length = 24;

        // Only the word past the prefix is varied, so the two walk through slots independently.
        AssertOnlyTheSlotHolderIsAnswered(word =>
        {
            byte[] holder = Pattern(length, seed: 53);
            BinaryPrimitives.WriteUInt32LittleEndian(holder.AsSpan(length - sizeof(ulong)), word);
            return (holder, holder[..(length - sizeof(ulong))]);
        });
    }

    /// <remarks>
    /// Four keys per slot over every memoized length, so most are displaced. A survivor answering with
    /// another key's digest is the aliasing a direct-mapped memo with no byte-granular compare has to
    /// rule out, whatever mix of lengths shares its slot.
    /// </remarks>
    [Test]
    public void Memo_never_answers_for_a_key_it_no_longer_holds()
    {
        const int keys = 4 * SlotCount;

        byte[][] inputs = new byte[keys][];
        Random random = new(29);
        for (int i = 0; i < keys; i++)
        {
            inputs[i] = new byte[MinLength + i % (MaxLength - MinLength + 1)];
            random.NextBytes(inputs[i]);
            KeccakCache.WriteMemo(inputs[i], Digest(i));
        }

        int survivors = 0;
        for (int i = 0; i < keys; i++)
        {
            if (KeccakCache.TryReadMemo(inputs[i], out ValueHash256 read))
            {
                survivors++;
                if (read != Digest(i))
                {
                    Assert.Fail($"{inputs[i].Length}-byte key {i} read another key's digest");
                }
            }
        }

        Assert.That(survivors, Is.GreaterThan(0), "nothing survived, so nothing was verified");
    }

    /// <summary>
    /// Searches <paramref name="variation"/> for a pair sharing a slot, then asserts that storing a digest
    /// for the holder leaves its partner a miss rather than an answer.
    /// </summary>
    /// <param name="variation">
    /// Builds a candidate pair from a seed. Both halves are rebuilt per seed, so the pair the search
    /// matched is the pair the assertions run against.
    /// </param>
    /// <remarks>
    /// The slot index is not observable from here, so a shared slot is told from one input displacing
    /// the other, and the search asserting that it found a pair is what keeps the case from passing
    /// vacuously.
    /// </remarks>
    private static void AssertOnlyTheSlotHolderIsAnswered(Func<uint, (byte[] Holder, byte[] Partner)> variation)
    {
        (byte[] Holder, byte[] Partner)? pair = null;
        for (uint candidate = 1; candidate < 8 * SlotCount && pair is null; candidate++)
        {
            (byte[] holder, byte[] partner) = variation(candidate);
            if (SharesASlot(holder, partner))
            {
                pair = (holder, partner);
            }
        }

        Assert.That(pair, Is.Not.Null, "no candidate landed in the same slot");

        KeccakCache.WriteMemo(pair.Value.Holder, Digest(pair.Value.Holder.Length));

        Assert.That(KeccakCache.TryReadMemo(pair.Value.Partner, out _), Is.False);
    }

    /// <summary>Whether the two inputs share a slot, told from one displacing the other.</summary>
    private static bool SharesASlot(byte[] first, byte[] second)
    {
        KeccakCache.WriteMemo(first, Digest(1));
        KeccakCache.WriteMemo(second, Digest(2));
        return !KeccakCache.TryReadMemo(first, out _);
    }

    private static byte[] Pattern(int length, int seed)
    {
        byte[] input = new byte[length];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = (byte)(seed + i * 7);
        }

        return input;
    }

    /// <summary>A digest standing in for a keccak, distinct per <paramref name="seed"/> and never default.</summary>
    private static ValueHash256 Digest(int seed)
    {
        byte[] digest = new byte[ValueHash256.MemorySize];
        BinaryPrimitives.WriteInt32LittleEndian(digest, seed);
        digest[^1] = 0xFF;
        return new ValueHash256(digest);
    }
}
