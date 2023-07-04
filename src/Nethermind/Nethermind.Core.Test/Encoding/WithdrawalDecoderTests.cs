// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
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

        rlp.ToHexString().Should().BeEquivalentTo("d8010294fffffffffffffffffffffffffffffffffffffffe03");
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

        decoded.Should().BeEquivalentTo(withdrawal);
    }

    [Test]
    public void Should_decode_with_ValueDecoderContext()
    {
        Withdrawal withdrawal = new()
        {
            Index = long.MaxValue,
            ValidatorIndex = int.MaxValue,
            Address = new Address("0x773f86fb098bb19f228f441a7715daa13d10a751"),
            AmountInGwei = ulong.MaxValue
        };
        RlpStream stream = new(1024);
        WithdrawalDecoder codec = new();

        codec.Encode(stream, withdrawal);

        Rlp.ValueDecoderContext decoderContext = new(stream.Data.AsSpan());
        Withdrawal? decoded = codec.Decode(ref decoderContext);

        decoded.Should().BeEquivalentTo(withdrawal);
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

        rlp1.Should().BeEquivalentTo(rlp2);
    }
}
