// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.ConsensusRequests;

namespace Nethermind.Serialization.Rlp;

public class ValidatorExitsDecoder : IRlpStreamDecoder<WithdrawalRequest>, IRlpValueDecoder<WithdrawalRequest>, IRlpObjectDecoder<WithdrawalRequest>
{
    public int GetLength(WithdrawalRequest item, RlpBehaviors rlpBehaviors) =>
        Rlp.LengthOfSequence(Rlp.LengthOf(item.SourceAddress) + Rlp.LengthOf(item.ValidatorPubkey)) +
        Rlp.LengthOf(item.Amount);

    public WithdrawalRequest Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int _ = rlpStream.ReadSequenceLength();
        Address sourceAddress = rlpStream.DecodeAddress();
        ArgumentNullException.ThrowIfNull(sourceAddress);
        byte[] validatorPubkey = rlpStream.DecodeByteArray();
        ulong amount = rlpStream.DecodeULong();
        return new WithdrawalRequest()
        {
            SourceAddress = sourceAddress,
            ValidatorPubkey = validatorPubkey,
            Amount = amount
        };
    }

    public WithdrawalRequest Decode(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int _ = decoderContext.ReadSequenceLength();
        Address sourceAddress = decoderContext.DecodeAddress();
        ArgumentNullException.ThrowIfNull(sourceAddress);
        byte[] validatorPubkey = decoderContext.DecodeByteArray();
        ulong amount = decoderContext.DecodeULong();
        return new WithdrawalRequest()
        {
            SourceAddress = sourceAddress,
            ValidatorPubkey = validatorPubkey,
            Amount = amount
        };
    }

    public void Encode(RlpStream stream, WithdrawalRequest item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int contentLength = GetLength(item, rlpBehaviors);
        stream.StartSequence(contentLength);
        stream.Encode(item.SourceAddress);
        stream.Encode(item.ValidatorPubkey);
        stream.Encode(item.Amount);
    }

    public Rlp Encode(WithdrawalRequest item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int contentLength = GetLength(item, rlpBehaviors);
        RlpStream rlpStream = new(Rlp.LengthOfSequence(contentLength));

        Encode(rlpStream, item, rlpBehaviors);

        return new Rlp(rlpStream.Data.ToArray());
    }
}
