// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.ConsensusRequests;

namespace Nethermind.Serialization.Rlp;

public class ConsolidationRequestDecoder : IRlpStreamDecoder<ConsolidationRequest>, IRlpValueDecoder<ConsolidationRequest>, IRlpObjectDecoder<ConsolidationRequest>
{
    public int GetLength(ConsolidationRequest item, RlpBehaviors rlpBehaviors) =>
        Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));

    public int GetContentLength(ConsolidationRequest item, RlpBehaviors rlpBehaviors) =>
        Rlp.LengthOf(item.SourceAddress) + Rlp.LengthOf(item.SourcePubKey) +
        Rlp.LengthOf(item.TargetPubKey);

    public ConsolidationRequest Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int _ = rlpStream.ReadSequenceLength();
        Address sourceAddress = rlpStream.DecodeAddress();
        ArgumentNullException.ThrowIfNull(sourceAddress);
        byte[] sourcePubKey = rlpStream.DecodeByteArray();
        byte[] targetPubKey = rlpStream.DecodeByteArray();
        return new ConsolidationRequest()
        {
            SourceAddress = sourceAddress,
            SourcePubKey = sourcePubKey,
            TargetPubKey = targetPubKey
        };
    }

    public ConsolidationRequest Decode(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int _ = decoderContext.ReadSequenceLength();
        Address sourceAddress = decoderContext.DecodeAddress();
        ArgumentNullException.ThrowIfNull(sourceAddress);
        byte[] sourcePubKey = decoderContext.DecodeByteArray();
        byte[] targetPubKey = decoderContext.DecodeByteArray();
        return new ConsolidationRequest()
        {
            SourceAddress = sourceAddress,
            SourcePubKey = sourcePubKey,
            TargetPubKey = targetPubKey
        };
    }

    public void Encode(RlpStream stream, ConsolidationRequest item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int contentLength = GetContentLength(item, rlpBehaviors);
        stream.StartSequence(contentLength);
        stream.Encode(item.SourceAddress);
        stream.Encode(item.SourcePubKey);
        stream.Encode(item.TargetPubKey);
    }

    public Rlp Encode(ConsolidationRequest item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        RlpStream rlpStream = new(GetLength(item, rlpBehaviors));

        Encode(rlpStream, item, rlpBehaviors);

        return new Rlp(rlpStream.Data.ToArray());
    }
}
