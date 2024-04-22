// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.ConsensusRequests;

namespace Nethermind.Serialization.Rlp;

public class ConsensusRequestDecoder : IRlpStreamDecoder<ConsensusRequest>, IRlpValueDecoder<ConsensusRequest>, IRlpObjectDecoder<ConsensusRequest>
{
    private readonly WithdrawalRequestDecoder _withdrawalRequestDecoder = new();
    private readonly DepositDecoder _depositDecoder = new();
    public int GetContentLength(ConsensusRequest item, RlpBehaviors rlpBehaviors)
    {
        int length = Rlp.LengthOf((byte)item.Type);
        switch (item.Type)
        {
            case ConsensusRequestsType.WithdrawalRequest:
                length += _withdrawalRequestDecoder.GetContentLength((WithdrawalRequest)item, rlpBehaviors);
                break;
            case ConsensusRequestsType.Deposit:
                length += _depositDecoder.GetContentLength((Deposit)item, rlpBehaviors);
                break;
        }
        return length;
    }

    public int GetLength(ConsensusRequest item, RlpBehaviors rlpBehaviors)
    {
        return Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));
    }

    public ConsensusRequest? Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (rlpStream.IsNextItemNull())
        {
            rlpStream.ReadByte();
            return null;
        }

        rlpStream.ReadSequenceLength();

        ConsensusRequestsType consensusRequestsType = (ConsensusRequestsType)rlpStream.ReadByte();

        Console.WriteLine("ConsensusRequestsType: " + consensusRequestsType);

        ConsensusRequest result = consensusRequestsType switch
        {
            ConsensusRequestsType.WithdrawalRequest => _withdrawalRequestDecoder.Decode(rlpStream, rlpBehaviors),
            ConsensusRequestsType.Deposit => _depositDecoder.Decode(rlpStream, rlpBehaviors),

            _ => throw new RlpException($"Unsupported consensus request type {consensusRequestsType}")
        };

        return result;
    }

    public ConsensusRequest Decode(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (decoderContext.IsNextItemNull())
        {
            decoderContext.ReadByte();
            return null;
        }

        decoderContext.ReadSequenceLength();

        ConsensusRequestsType consensusRequestsType = (ConsensusRequestsType)decoderContext.ReadByte();

        ConsensusRequest result = consensusRequestsType switch
        {
            ConsensusRequestsType.WithdrawalRequest => _withdrawalRequestDecoder.Decode(ref decoderContext, rlpBehaviors),
            ConsensusRequestsType.Deposit => _depositDecoder.Decode(ref decoderContext, rlpBehaviors),
            _ => throw new RlpException($"Unsupported consensus request type {consensusRequestsType}")
        };

        return result;
    }

    public void Encode(RlpStream stream, ConsensusRequest item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        stream.StartSequence(GetLength(item, rlpBehaviors));
        stream.WriteByte((byte)item.Type);
        switch (item.Type)
        {
            case ConsensusRequestsType.WithdrawalRequest:
                _withdrawalRequestDecoder.Encode(stream, (WithdrawalRequest)item, rlpBehaviors);
                break;
            case ConsensusRequestsType.Deposit:
                _depositDecoder.Encode(stream, (Deposit)item, rlpBehaviors);
                break;
        }
    }

    public Rlp Encode(ConsensusRequest item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        RlpStream rlpStream = new RlpStream(GetLength(item, rlpBehaviors));
        // debug getLength
        Console.WriteLine("GetLength: " + GetLength(item, rlpBehaviors));
        Encode(rlpStream, item, rlpBehaviors);
        return new Rlp(rlpStream.Data.ToArray());
    }
}
