// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.ConsensusRequests;

namespace Nethermind.Serialization.Rlp;

public class ConsolidationRequestDecoder : IRlpStreamDecoder<ConsolidationRequest>, IRlpValueDecoder<ConsolidationRequest>, IRlpObjectDecoder<ConsolidationRequest>
{
    public static ConsolidationRequestDecoder Instance { get; } = new();
    
    public int GetLength(ConsolidationRequest item, RlpBehaviors rlpBehaviors) =>
        Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));

    public int GetContentLength(ConsolidationRequest item, RlpBehaviors rlpBehaviors) =>
        Rlp.LengthOf(item.SourceAddress) + Rlp.LengthOf(item.SourcePubkey) +
        Rlp.LengthOf(item.TargetPubkey);

    public ConsolidationRequest Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int _ = rlpStream.ReadSequenceLength();
        Address sourceAddress = rlpStream.DecodeAddress();
        ArgumentNullException.ThrowIfNull(sourceAddress);
        byte[] SourcePubkey = rlpStream.DecodeByteArray();
        byte[] TargetPubkey = rlpStream.DecodeByteArray();
        return new ConsolidationRequest()
        {
            SourceAddress = sourceAddress,
            SourcePubkey = SourcePubkey,
            TargetPubkey = TargetPubkey
        };
    }

    public ConsolidationRequest Decode(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int _ = decoderContext.ReadSequenceLength();
        Address sourceAddress = decoderContext.DecodeAddress();
        ArgumentNullException.ThrowIfNull(sourceAddress);
        byte[] SourcePubkey = decoderContext.DecodeByteArray();
        byte[] TargetPubkey = decoderContext.DecodeByteArray();
        return new ConsolidationRequest()
        {
            SourceAddress = sourceAddress,
            SourcePubkey = SourcePubkey,
            TargetPubkey = TargetPubkey
        };
    }

    public void Encode(RlpStream stream, ConsolidationRequest item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int contentLength = GetContentLength(item, rlpBehaviors);
        stream.StartSequence(contentLength);
        stream.Encode(item.SourceAddress);
        stream.Encode(item.SourcePubkey);
        stream.Encode(item.TargetPubkey);
    }

    public Rlp Encode(ConsolidationRequest item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        RlpStream rlpStream = new(GetLength(item, rlpBehaviors));

        Encode(rlpStream, item, rlpBehaviors);

        return new Rlp(rlpStream.Data.ToArray());
    }
}
