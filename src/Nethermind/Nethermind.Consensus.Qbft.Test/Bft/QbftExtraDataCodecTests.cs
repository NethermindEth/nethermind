// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Consensus.Qbft.Test.Bft;

/// <summary>Port of Besu's <c>QbftExtraDataCodecTest</c>, including its known raw encoding.</summary>
[Parallelizable(ParallelScope.All)]
public class QbftExtraDataCodecTests
{
    private const string RawHexEncoding =
        "0xf8f0a00102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20ea940000000000000000"
        + "000000000000000000000001940000000000000000000000000000000000000002d7940000000000000000"
        + "00000000000000000000000181ff83fedcbaf886b841000000000000000000000000000000000000000000"
        + "0000000000000000000001000000000000000000000000000000000000000000000000000000000000000a"
        + "00b841000000000000000000000000000000000000000000000000000000000000000a0000000000000000"
        + "00000000000000000000000000000000000000000000000100";

    private const int Round = 0x00FEDCBA;
    private readonly QbftExtraDataCodec _codec = QbftExtraDataCodec.Instance;

    private static BftExtraData KnownExtraData() => new(
        QbftTestData.NonEmptyVanity(),
        [QbftTestData.Seal(1, 10, 0), QbftTestData.Seal(10, 1, 0)],
        Vote.AuthVote(QbftTestData.Addr(1)),
        Round,
        [QbftTestData.Addr(1), QbftTestData.Addr(2)]);

    [Test]
    public void EncodingMatchesKnownRawHexString() =>
        Assert.That(_codec.Encode(KnownExtraData()), Is.EqualTo(Bytes.FromHexString(RawHexEncoding)));

    [Test]
    public void DecodingOfKnownRawHexStringMatchesKnownExtraDataObject() =>
        AssertExtraData(_codec.Decode(Bytes.FromHexString(RawHexEncoding)), KnownExtraData());

    public static IEnumerable<TestCaseData> RoundTripCases()
    {
        yield return new TestCaseData(new BftExtraData(QbftTestData.ZeroVanity(), [], null, Round, [])).SetName("EmptyVoteAndLists");
        yield return new TestCaseData(new BftExtraData(QbftTestData.ZeroVanity(), [], Vote.AuthVote(QbftTestData.Addr(1)), Round, [])).SetName("EmptyListsWithVote");
        yield return new TestCaseData(new BftExtraData(QbftTestData.ZeroVanity(), [], Vote.DropVote(QbftTestData.Addr(1)), Round, [])).SetName("EmptyListsWithDropVote");
        yield return new TestCaseData(KnownExtraData()).SetName("FullyPopulated");
    }

    [TestCaseSource(nameof(RoundTripCases))]
    public void IsEncodedAndDecodedCorrectly(BftExtraData expected) =>
        AssertExtraData(_codec.Decode(_codec.Encode(expected)), expected);

    [Test]
    public void CorrectlyCodedRoundConstitutesValidContent()
    {
        // Round encoded as two non-fixed-length bytes.
        byte[] encoded = Encode(QbftTestData.ZeroVanity(), [], (ref RlpWriter writer) => EncodeVote(ref writer, QbftTestData.Addr(1), true), [0xDC, 0xBA], []);
        BftExtraData extraData = _codec.Decode(encoded);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(extraData.Round, Is.EqualTo(0xDCBA));
            Assert.That(extraData.Seals, Is.Empty);
            Assert.That(extraData.Validators, Is.Empty);
        }
    }

    [Test]
    public void IncorrectlyEncodedRoundThrowsRlpException()
    {
        // The round in fixed 4-byte format with a leading zero is not a canonical scalar; the vote is also not a list here.
        byte[] encoded = Encode(QbftTestData.ZeroVanity(), [], (ref RlpWriter writer) =>
        {
            writer.Encode(QbftTestData.Addr(1));
            writer.Encode(Vote.AddByteValue);
        }, [0x00, 0xDC, 0xBA], []);
        Assert.That(() => _codec.Decode(encoded), Throws.InstanceOf<RlpException>());
    }

    [Test]
    public void NullVoteAndListConstituteValidContent()
    {
        byte[] encoded = Encode(QbftTestData.ZeroVanity(), [], static (ref RlpWriter writer) => writer.EncodeNullObject(), Scalar(Round), []);
        BftExtraData extraData = _codec.Decode(encoded);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(extraData.Vote, Is.Null);
            Assert.That(extraData.Round, Is.EqualTo(Round));
        }
    }

    [Test]
    public void DropVoteIsAnEmptyByteString()
    {
        byte[] encoded = Encode(QbftTestData.ZeroVanity(), [], (ref RlpWriter writer) => EncodeVote(ref writer, QbftTestData.Addr(1), false), Scalar(Round), []);
        BftExtraData extraData = _codec.Decode(encoded);
        Assert.That(extraData.Vote, Is.EqualTo(Vote.DropVote(QbftTestData.Addr(1))));
    }

    [Test]
    public void ExtraDataCanBeEncodedWithoutCommitSeals()
    {
        BftExtraData extraData = KnownExtraData();
        byte[] expected = Encode(extraData.VanityData, extraData.Validators, (ref RlpWriter writer) => EncodeVote(ref writer, QbftTestData.Addr(1), true), Scalar(Round), []);
        Assert.That(_codec.EncodeWithoutCommitSeals(extraData), Is.EqualTo(expected));
    }

    [Test]
    public void ExtraDataCanBeEncodedWithoutCommitSealsOrRoundNumber()
    {
        BftExtraData extraData = KnownExtraData();
        byte[] expected = Encode(extraData.VanityData, extraData.Validators, (ref RlpWriter writer) => EncodeVote(ref writer, QbftTestData.Addr(1), true), [], []);
        Assert.That(_codec.EncodeWithoutCommitSealsAndRound(extraData), Is.EqualTo(expected));
    }

    [Test]
    public void TrailingItemsAreIgnoredLikeBesuLeaveListLenient()
    {
        byte[] encoded = Encode(QbftTestData.ZeroVanity(), [], (ref RlpWriter writer) => EncodeVote(ref writer, QbftTestData.Addr(1), true), Scalar(Round), [], trailingItem: 1);
        BftExtraData extraData = _codec.Decode(encoded);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(extraData.Round, Is.EqualTo(Round));
            Assert.That(extraData.Vote, Is.EqualTo(Vote.AuthVote(QbftTestData.Addr(1))));
            Assert.That(_codec.Encode(extraData), Is.Not.EqualTo(encoded), "the canonical re-encoding drops the trailing item");
        }
    }

    [Test]
    public void IncorrectVoteTypeThrowsException()
    {
        byte[] encoded = Encode(QbftTestData.ZeroVanity(), [QbftTestData.Addr(1), QbftTestData.Addr(2)], (ref RlpWriter writer) =>
        {
            writer.StartSequence(Rlp.LengthOfAddressRlp + 2);
            writer.Encode(QbftTestData.Addr(1));
            writer.Encode((byte)0xAA);
        }, Scalar(Round), []);
        Assert.That(() => _codec.Decode(encoded), Throws.InstanceOf<RlpException>());
    }

    [Test]
    public void EmptyExtraDataThrowsException() =>
        Assert.That(() => _codec.Decode(ReadOnlySpan<byte>.Empty), Throws.InstanceOf<RlpException>().With.Message.EqualTo("Invalid bytes supplied - BFT extra data required."));

    public static void AssertExtraData(BftExtraData actual, BftExtraData expected)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual.VanityData, Is.EqualTo(expected.VanityData), "vanity");
            Assert.That(actual.Validators, Is.EqualTo(expected.Validators), "validators");
            Assert.That(actual.Vote, Is.EqualTo(expected.Vote), "vote");
            Assert.That(actual.Round, Is.EqualTo(expected.Round), "round");
            Assert.That(actual.Seals, Is.EqualTo(expected.Seals), "seals");
        }
    }

    private delegate void VoteWriter(ref RlpWriter writer);

    /// <summary>Minimal big-endian bytes of <paramref name="value"/>, the content of an RLP scalar.</summary>
    private static byte[] Scalar(int value) => value == 0 ? [] : ((ulong)value).ToBigEndianByteArray().WithoutLeadingZeros().ToArray();

    /// <summary>Hand-rolled Besu-style encoding: <paramref name="roundBytes"/> is written as a byte string (empty = the round item is a 0x80 "null"), <paramref name="trailingItem"/> adds an extra list item.</summary>
    private static byte[] Encode(byte[] vanity, IReadOnlyList<Address> validators, VoteWriter vote, byte[] roundBytes, byte[][] seals, int? trailingItem = null)
    {
        byte[] probe = new byte[4096];
        RlpWriter counter = new(probe);
        WriteBody(ref counter, vanity, validators, vote, roundBytes, seals, trailingItem);
        int contentLength = counter.Position;

        byte[] result = new byte[Rlp.LengthOfSequence(contentLength)];
        RlpWriter writer = new(result);
        writer.StartSequence(contentLength);
        WriteBody(ref writer, vanity, validators, vote, roundBytes, seals, trailingItem);
        return result;
    }

    private static void WriteBody(ref RlpWriter writer, byte[] vanity, IReadOnlyList<Address> validators, VoteWriter vote, byte[] roundBytes, byte[][] seals, int? trailingItem)
    {
        writer.Encode(vanity);
        writer.StartSequence(validators.Count * Rlp.LengthOfAddressRlp);
        foreach (Address validator in validators) writer.Encode(validator);
        vote(ref writer);
        writer.Encode(roundBytes);
        int sealsLength = 0;
        foreach (byte[] seal in seals) sealsLength += Rlp.LengthOf(seal);
        writer.StartSequence(sealsLength);
        foreach (byte[] seal in seals) writer.Encode(seal);
        if (trailingItem is { } item) writer.Encode(item);
    }

    private static void EncodeVote(ref RlpWriter writer, Address recipient, bool add)
    {
        writer.StartSequence(Rlp.LengthOfAddressRlp + (add ? 2 : 1));
        writer.Encode(recipient);
        if (add) writer.Encode(Vote.AddByteValue);
        else writer.EncodeEmptyByteArray();
    }
}

/// <summary>The IBFT 2.0 shape differs only in the vote, round and omitted-seal encodings.</summary>
[Parallelizable(ParallelScope.All)]
public class Ibft2ExtraDataCodecTests
{
    private readonly Ibft2ExtraDataCodec _codec = Ibft2ExtraDataCodec.Instance;

    public static IEnumerable<TestCaseData> RoundTripCases()
    {
        yield return new TestCaseData(new BftExtraData(QbftTestData.ZeroVanity(), [], null, 0, [])).SetName("Empty");
        yield return new TestCaseData(new BftExtraData(QbftTestData.NonEmptyVanity(), [QbftTestData.Seal(1, 10, 0)], Vote.AuthVote(QbftTestData.Addr(1)), 0x00FEDCBA, [QbftTestData.Addr(1), QbftTestData.Addr(2)])).SetName("AuthVote");
        yield return new TestCaseData(new BftExtraData(QbftTestData.NonEmptyVanity(), [QbftTestData.Seal(10, 1, 1)], Vote.DropVote(QbftTestData.Addr(2)), 3, [QbftTestData.Addr(2)])).SetName("DropVote");
    }

    [TestCaseSource(nameof(RoundTripCases))]
    public void IsEncodedAndDecodedCorrectly(BftExtraData expected) =>
        QbftExtraDataCodecTests.AssertExtraData(_codec.Decode(_codec.Encode(expected)), expected);

    [Test]
    public void RoundIsFixedFourBytesAndAbsentVoteIsEmptyString()
    {
        byte[] encoded = _codec.Encode(new BftExtraData(QbftTestData.ZeroVanity(), [], null, 1, []));
        // [vanity(32), [], "", 0x00000001, []]
        Assert.That(encoded.ToHexString(), Is.EqualTo("e9a00000000000000000000000000000000000000000000000000000000000000000c080840000000" + "1c0"));
    }

    [Test]
    public void DigestsOmitTrailingItemsInsteadOfWritingThemEmpty()
    {
        BftExtraData extraData = new(QbftTestData.ZeroVanity(), [QbftTestData.Seal(1, 10, 0)], null, 5, []);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_codec.EncodeWithoutCommitSeals(extraData).ToHexString(), Is.EqualTo("e8a00000000000000000000000000000000000000000000000000000000000000000c0808400000005"));
            Assert.That(_codec.EncodeWithoutCommitSealsAndRound(extraData).ToHexString(), Is.EqualTo("e3a00000000000000000000000000000000000000000000000000000000000000000c080"));
        }
    }

    [Test]
    public void DropVoteIsARawZeroByte()
    {
        byte[] encoded = _codec.Encode(new BftExtraData(QbftTestData.ZeroVanity(), [], Vote.DropVote(QbftTestData.Addr(1)), 0, []));
        // vote item: d5 94 <addr> 00
        Assert.That(encoded.ToHexString(), Does.Contain("d69400000000000000000000000000000000000000010084"));
    }
}
