// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core.ConsensusRequests;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Tracing.GethStyle.Custom.JavaScript;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;
using Nethermind.Core.Test.Builders;

namespace Nethermind.Core.Test.Encoding;

public class ConsensusRequestDecoderTests
{
    [Test]
    public void Roundtrip_deposit()
    {
        ConsensusRequest deposit = new Deposit()
        {
            Index = long.MaxValue,
            PubKey = KeccakTests.KeccakOfAnEmptyString.ToBytes(),
            Signature = KeccakTests.KeccakOfAnEmptyString.ToBytes(),
            WithdrawalCredentials = KeccakTests.KeccakOfAnEmptyString.ToBytes(),
            Amount = int.MaxValue
        };

        byte[] rlp = Rlp.Encode(deposit).Bytes;
        ConsensusRequest decoded = Rlp.Decode<ConsensusRequest>(rlp);

        decoded.Should().BeEquivalentTo(deposit);
    }

    [Test]
    public void Roundtrip_withdrawalRequest()
    {
        byte[] validatorPubkey = new byte[48];
        validatorPubkey[11] = 11;
        ConsensusRequest withdrawalRequest = new WithdrawalRequest()
        {
            SourceAddress = TestItem.AddressA,
            ValidatorPubkey = validatorPubkey,
            Amount = int.MaxValue
        };

        byte[] rlp = Rlp.Encode(withdrawalRequest).Bytes;
        ConsensusRequest decoded = Rlp.Decode<ConsensusRequest>(rlp);

        decoded.Should().BeEquivalentTo(withdrawalRequest);
    }

    [Test]
    public void Should_decode_deposit_with_ValueDecoderContext()
    {
        ConsensusRequest deposit = new Deposit()
        {
            Index = long.MaxValue,
            PubKey = KeccakTests.KeccakOfAnEmptyString.ToBytes(),
            Signature = KeccakTests.KeccakOfAnEmptyString.ToBytes(),
            WithdrawalCredentials = KeccakTests.KeccakOfAnEmptyString.ToBytes(),
            Amount = int.MaxValue
        };
        RlpStream stream = new(1024);
        ConsensusRequestDecoder codec = new();

        codec.Encode(stream, deposit);

        Rlp.ValueDecoderContext decoderContext = new(stream.Data.AsSpan());
        Deposit? decoded = (Deposit?)codec.Decode(ref decoderContext);

        decoded.Should().BeEquivalentTo(deposit);
    }

    [Test]
    public void Should_decode_withdrawalRequest_with_ValueDecoderContext()
    {
        ConsensusRequest withdrawalRequest = new WithdrawalRequest()
        {
            SourceAddress = TestItem.AddressA,
            ValidatorPubkey = KeccakTests.KeccakOfAnEmptyString.ToBytes(),
            Amount = int.MaxValue
        };
        RlpStream stream = new(1024);
        ConsensusRequestDecoder codec = new();

        codec.Encode(stream, withdrawalRequest);

        Rlp.ValueDecoderContext decoderContext = new(stream.Data.AsSpan());
        WithdrawalRequest? decoded = (WithdrawalRequest?)codec.Decode(ref decoderContext);

        decoded.Should().BeEquivalentTo(withdrawalRequest);
    }

    [Test]
    public void Should_encode_deposit_same_for_Rlp_Encode_and_DepositDecoder_Encode()
    {
        ConsensusRequest deposit = new Deposit()
        {
            Index = long.MaxValue,
            PubKey = KeccakTests.KeccakOfAnEmptyString.ToBytes(),
            Signature = KeccakTests.KeccakOfAnEmptyString.ToBytes(),
            WithdrawalCredentials = KeccakTests.KeccakOfAnEmptyString.ToBytes(),
            Amount = int.MaxValue
        };
        byte[] rlp1 = new ConsensusRequestDecoder().Encode(deposit).Bytes;
        byte[] rlp2 = Rlp.Encode(deposit).Bytes;

        rlp1.Should().BeEquivalentTo(rlp2);
    }

    [Test]
    public void Should_encode_withdrawalRequest_same_for_Rlp_Encode_and_DepositDecoder_Encode()
    {
        ConsensusRequest withdrawalRequest = new WithdrawalRequest()
        {
            SourceAddress = TestItem.AddressA,
            ValidatorPubkey = KeccakTests.KeccakOfAnEmptyString.ToBytes(),
            Amount = int.MaxValue
        };
        byte[] rlp1 = new ConsensusRequestDecoder().Encode(withdrawalRequest).Bytes;
        byte[] rlp2 = Rlp.Encode(withdrawalRequest).Bytes;

        rlp1.Should().BeEquivalentTo(rlp2);
    }

    [Test]
    public void Should_encode_ConsensusRequests_Array()
    {
        ConsensusRequest[] requests = new ConsensusRequest[]
        {
            new Deposit()
            {
                Index = long.MaxValue,
                PubKey = KeccakTests.KeccakOfAnEmptyString.ToBytes(),
                Signature = KeccakTests.KeccakOfAnEmptyString.ToBytes(),
                WithdrawalCredentials = KeccakTests.KeccakOfAnEmptyString.ToBytes(),
                Amount = int.MaxValue
            },
            new WithdrawalRequest()
            {
                SourceAddress = TestItem.AddressA,
                ValidatorPubkey = KeccakTests.KeccakOfAnEmptyString.ToBytes(),
                Amount = int.MaxValue
            }
        };

        byte[] rlp = Rlp.Encode(requests).Bytes;
        RlpStream rlpStream = new(rlp);
        ConsensusRequest[] decoded = Rlp.DecodeArray(rlpStream, new ConsensusRequestDecoder());
        decoded.Should().BeEquivalentTo(requests);
    }
}
