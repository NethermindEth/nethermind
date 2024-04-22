// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core.ConsensusRequests;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Tracing.GethStyle.Custom.JavaScript;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding;

public class DepositDecoderTests
{
    [Test]
    public void Should_decode()
    {
        Deposit Deposit = new()
        {
            Index = long.MaxValue,
            PubKey = KeccakTests.KeccakOfAnEmptyString.ToBytes(),
            Signature = KeccakTests.KeccakOfAnEmptyString.ToBytes(),
            WithdrawalCredentials = KeccakTests.KeccakOfAnEmptyString.ToBytes(),
            Amount = int.MaxValue
        };
        byte[] rlp = Rlp.Encode(Deposit).Bytes;
        Deposit decoded = Rlp.Decode<Deposit>(rlp);

        decoded.Should().BeEquivalentTo(Deposit);
    }

    [Test]
    public void Should_decode_with_ValueDecoderContext()
    {
        Deposit Deposit = new()
        {
            Index = long.MaxValue,
            PubKey = KeccakTests.KeccakOfAnEmptyString.ToBytes(),
            Signature = KeccakTests.KeccakOfAnEmptyString.ToBytes(),
            WithdrawalCredentials = KeccakTests.KeccakOfAnEmptyString.ToBytes(),
            Amount = int.MaxValue
        };
        RlpStream stream = new(1024);
        DepositDecoder codec = new();

        codec.Encode(stream, Deposit);

        Rlp.ValueDecoderContext decoderContext = new(stream.Data.AsSpan());
        Deposit? decoded = codec.Decode(ref decoderContext);

        decoded.Should().BeEquivalentTo(Deposit);
    }

    [Test]
    public void Should_encode_same_for_Rlp_Encode_and_DepositDecoder_Encode()
    {
        Deposit Deposit = new()
        {
            Index = long.MaxValue,
            PubKey = KeccakTests.KeccakOfAnEmptyString.ToBytes(),
            Signature = KeccakTests.KeccakOfAnEmptyString.ToBytes(),
            WithdrawalCredentials = KeccakTests.KeccakOfAnEmptyString.ToBytes(),
            Amount = int.MaxValue
        };
        byte[] rlp1 = new DepositDecoder().Encode(Deposit).Bytes;
        byte[] rlp2 = Rlp.Encode(Deposit).Bytes;

        rlp1.Should().BeEquivalentTo(rlp2);
    }
}
