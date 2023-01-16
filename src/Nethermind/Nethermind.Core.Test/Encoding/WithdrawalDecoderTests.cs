// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
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
        var withdrawal = new Withdrawal
        {
            Index = 1,
            ValidatorIndex = 2,
            Address = Address.SystemUser,
            Amount = 3
        };
        var rlp = Rlp.Encode(withdrawal).Bytes;

        rlp.ToHexString().Should().BeEquivalentTo("d8010294fffffffffffffffffffffffffffffffffffffffe03");
    }

    [Test]
    public void Should_decode()
    {
        var withdrawal = new Withdrawal
        {
            Index = 1,
            ValidatorIndex = 2,
            Address = new Address("0x773f86fb098bb19f228f441a7715daa13d10a751"),
            Amount = 3
        };
        var rlp = Rlp.Encode(withdrawal).Bytes;
        var decoded = Rlp.Decode<Withdrawal>(rlp);

        decoded.Should().BeEquivalentTo(withdrawal);
    }

    [Test]
    public void Should_decode_with_ValueDecoderContext()
    {
        var withdrawal = new Withdrawal
        {
            Index = long.MaxValue,
            ValidatorIndex = int.MaxValue,
            Address = new Address("0x773f86fb098bb19f228f441a7715daa13d10a751"),
            Amount = UInt256.UInt128MaxValue
        };
        var stream = new RlpStream(1024);
        var codec = new WithdrawalDecoder();

        codec.Encode(stream, withdrawal);

        var decoderContext = new Rlp.ValueDecoderContext(stream.Data.AsSpan());
        var decoded = codec.Decode(ref decoderContext);

        decoded.Should().BeEquivalentTo(withdrawal);
    }

    [Test]
    public void Should_encode_same_for_Rlp_Encode_and_WithdrawalDecoder_Encode()
    {
        var withdrawal = new Withdrawal
        {
            Index = long.MaxValue,
            ValidatorIndex = int.MaxValue,
            Address = new Address("0x7e24b8f924a82df020eef45c320deb224559f13e"),
            Amount = UInt256.UInt128MaxValue
        };
        var rlp1 = new WithdrawalDecoder().Encode(withdrawal).Bytes;
        var rlp2 = Rlp.Encode(withdrawal).Bytes;

        rlp1.Should().BeEquivalentTo(rlp2);
    }
}
