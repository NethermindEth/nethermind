// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding;

public class WithdrawalDecoderTests
{
    [Test]
    public void Should_encode()
    {
        Withdrawal withdrawal = new()
        {
            Index = 1,
            ValidatorIndex = 2,
            Address = Address.SystemUser,
            AmountInGwei = 3
        };
        byte[] rlp = Rlp.Encode(withdrawal).Bytes;

        Assert.That(rlp.ToHexString(), Is.EqualTo("d8010294fffffffffffffffffffffffffffffffffffffffe03"));
    }

    [Test]
    public void Should_decode()
    {
        Withdrawal withdrawal = new()
        {
            Index = 1,
            ValidatorIndex = 2,
            Address = new Address("0x773f86fb098bb19f228f441a7715daa13d10a751"),
            AmountInGwei = 3
        };
        byte[] rlp = Rlp.Encode(withdrawal).Bytes;
        Withdrawal decoded = Rlp.Decode<Withdrawal>(rlp);

        Assert.That(decoded, Is.EqualTo(withdrawal).UsingWithdrawalComparer());
    }

    [Test]
    public void Should_decode_with_RlpReader()
    {
        Withdrawal withdrawal = new()
        {
            Index = long.MaxValue,
            ValidatorIndex = int.MaxValue,
            Address = new Address("0x773f86fb098bb19f228f441a7715daa13d10a751"),
            AmountInGwei = ulong.MaxValue
        };
        WithdrawalDecoder codec = new();
        byte[] bytes = new byte[codec.GetLength(withdrawal, RlpBehaviors.None)];
        RlpWriter writer = new(bytes);

        codec.Encode(ref writer, withdrawal);

        RlpReader decoderContext = new(bytes);
        Withdrawal? decoded = codec.Decode(ref decoderContext);

        Assert.That(decoded, Is.EqualTo(withdrawal).UsingWithdrawalComparer());
    }

    [Test]
    public void Should_encode_same_for_Rlp_Encode_and_WithdrawalDecoder_Encode()
    {
        Withdrawal withdrawal = new()
        {
            Index = long.MaxValue,
            ValidatorIndex = int.MaxValue,
            Address = new Address("0x7e24b8f924a82df020eef45c320deb224559f13e"),
            AmountInGwei = ulong.MaxValue
        };
        byte[] rlp1 = new WithdrawalDecoder().Encode(withdrawal).Bytes;
        byte[] rlp2 = Rlp.Encode(withdrawal).Bytes;

        Assert.That(rlp1, Is.EqualTo(rlp2));
    }

    /// <summary>
    /// The length (sequence header) of each item in the list must be validated, matching the behavior in other clients. <br/>
    /// Problematic RLP can be formed as this - instead of 2 correct withdrawals, encode a list of two "tampered" ones: <br/>
    /// - first will contain whole encoded <c>withdrawal_1</c> and starting part of <c>withdrawal_2</c>, <br/>
    /// - second one will contain remaining <c>withdrawal_2</c> part. <br/>
    /// Example: <c> [ [w1NoPrefix | w2NoAmount] | w2Amount ] </c> <br/>
    /// This "fools" outer array decoder - there are still 2 items, yet actual length of each item is encoded incorrectly. <br/>
    /// Other clients DO validate decoded length against position obtained after decoding all item's fields for each item in the array.
    /// </summary>
    [Test]
    public void Should_fail_on_cross_boundary_rlp()
    {
        WithdrawalDecoder decoder = new();

        Withdrawal withdrawal1 = new() { Index = 1, ValidatorIndex = 2, Address = TestItem.AddressA, AmountInGwei = 100 };
        Withdrawal withdrawal2 = new() { Index = 3, ValidatorIndex = 4, Address = TestItem.AddressB, AmountInGwei = 200 };

        byte[] w1 = decoder.Encode(withdrawal1).Bytes;
        byte[] w2 = decoder.Encode(withdrawal2).Bytes;

        int amountLen = Rlp.LengthOf(withdrawal2.AmountInGwei);
        byte[] w2NoAmount = w2[..^amountLen];
        byte[] w1NoPrefix = w1[1..];

        byte[] tamperedRlp1 = CombineRlpList(w1NoPrefix, w2NoAmount);
        byte[] tamperedRlp2 = w2[^amountLen..]; // raw bytes only (no list wrapper)

        byte[] rlp = CombineRlpList(tamperedRlp1, tamperedRlp2);

        void Decode() => new RlpReader(rlp).DecodeArray(decoder!);
        Assert.That(Decode, Throws.InstanceOf<RlpException>().And.Message.Contain("checkpoint failed"));
    }

    private static byte[] CombineRlpList(params byte[][] encodedItems)
    {
        int sequenceLength = encodedItems.Sum(static item => item.Length);
        byte[] result = new byte[Rlp.LengthOfSequence(sequenceLength)];

        int position = 0;
        position += Rlp.StartSequence(result, position, sequenceLength);
        foreach (byte[] item in encodedItems)
        {
            item.CopyTo(result.AsSpan(position));
            position += item.Length;
        }

        return result;
    }

}
