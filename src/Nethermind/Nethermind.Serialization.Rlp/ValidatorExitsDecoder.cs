// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.ValidatorExit;
using Nethermind.Core;

namespace Nethermind.Serialization.Rlp;

public class ValidatorExitsDecoder : IRlpStreamDecoder<ValidatorExit>, IRlpValueDecoder<ValidatorExit>, IRlpObjectDecoder<ValidatorExit>
{
    public int GetLength(ValidatorExit item, RlpBehaviors rlpBehaviors) =>
        Rlp.LengthOfSequence(Rlp.LengthOf(item.SourceAddress) + Rlp.LengthOf(item.ValidatorPubkey)) +
        Rlp.LengthOf(item.Amount);

    public ValidatorExit Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int _ = rlpStream.ReadSequenceLength();
        Address sourceAddress = rlpStream.DecodeAddress();
        ArgumentNullException.ThrowIfNull(sourceAddress);
        byte[] validatorPubkey = rlpStream.DecodeByteArray();
        ulong amount = rlpStream.DecodeULong();
        return new ValidatorExit(sourceAddress, validatorPubkey, amount);
    }

    public ValidatorExit Decode(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int _ = decoderContext.ReadSequenceLength();
        Address sourceAddress = decoderContext.DecodeAddress();
        ArgumentNullException.ThrowIfNull(sourceAddress);
        byte[] validatorPubkey = decoderContext.DecodeByteArray();
        ulong amount = decoderContext.DecodeULong();
        return new ValidatorExit(sourceAddress, validatorPubkey, amount);
    }

    public void Encode(RlpStream stream, ValidatorExit item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int contentLength = GetLength(item, rlpBehaviors);
        stream.StartSequence(contentLength);
        stream.Encode(item.SourceAddress);
        stream.Encode(item.ValidatorPubkey);
        stream.Encode(item.Amount);
    }

    public Rlp Encode(ValidatorExit item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int contentLength = GetLength(item, rlpBehaviors);
        RlpStream rlpStream = new(Rlp.LengthOfSequence(contentLength));

        Encode(rlpStream, item, rlpBehaviors);

        return new Rlp(rlpStream.Data.ToArray());
    }
}
