// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Eip2930;
using Nethermind.Core;
using Nethermind.Int256;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Serialization.Rlp.Eip7702;
public class SetCodeAuthorizationDecoder : IRlpStreamDecoder<SetCodeAuthorization[]?>, IRlpValueDecoder<SetCodeAuthorization[]?>
{
    public SetCodeAuthorization[]? Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (rlpStream.IsNextItemNull())
        {
            rlpStream.ReadByte();
            return null;
        }

        int outerLength = rlpStream.ReadSequenceLength();

        int check = rlpStream.Position + outerLength;

        List<SetCodeAuthorization> result = new List<SetCodeAuthorization>();

        while (rlpStream.Position < check)
        {
            //TODO check what is valid for the fields here
            byte[] contractCode = rlpStream.DecodeByteArray();
            result.Add(new SetCodeAuthorization(
                rlpStream.DecodeULong(),
                rlpStream.DecodeAddress(),
                rlpStream.DecodeUInt256(),
                rlpStream.DecodeULong(),
                rlpStream.DecodeByteArray(),
                rlpStream.DecodeByteArray()));
        }

        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
        {
            rlpStream.Check(check);
        }
        return result.ToArray();
    }

    public SetCodeAuthorization[]? Decode(
        ref Rlp.ValueDecoderContext decoderContext,
        RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (decoderContext.IsNextItemNull())
        {
            decoderContext.ReadByte();
            return null;
        }

        int outerLength = decoderContext.ReadSequenceLength();

        int check = decoderContext.Position + outerLength;

        List<SetCodeAuthorization> result = new List<SetCodeAuthorization>();

        while (decoderContext.Position < check)
        {
            //TODO check what is valid for the fields here
            byte[] contractCode = decoderContext.DecodeByteArray();
            result.Add(new SetCodeAuthorization(
                decoderContext.DecodeULong(),
                decoderContext.DecodeAddress(),
                decoderContext.DecodeUInt256(),
                decoderContext.DecodeULong(),
                decoderContext.DecodeByteArray(),
                decoderContext.DecodeByteArray()));
        }

        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
        {
            decoderContext.Check(check);
        }
        return result.ToArray();
    }

    public void Encode(RlpStream stream, SetCodeAuthorization[]? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            stream.WriteByte(Rlp.NullObjectByte);
            return;
        }

        int contentLength = GetContentLength(item);
        stream.StartSequence(contentLength);
        foreach (SetCodeAuthorization contractCode in item)
        {
            stream.Encode(contractCode.ChainId ?? throw new RlpException("Invalid tx set code format - chain id is null"));
            stream.Encode(contractCode.CodeAddress ?? throw new RlpException("Invalid tx set code format - address is null"));
            stream.Encode(contractCode.Nonce ?? 0);
            stream.Encode(contractCode.YParity);
            stream.Encode(contractCode.R);
            stream.Encode(contractCode.S);
        }
    }

    public int GetLength(SetCodeAuthorization[]? contractCodes, RlpBehaviors rlpBehaviors)
    {
        if (contractCodes is null)
        {
            return Rlp.OfEmptySequence.Length;
        }

        int contentLength = GetContentLength(contractCodes);
        return Rlp.LengthOfSequence(contentLength);
    }

    private static int GetContentLength(ReadOnlySpan<SetCodeAuthorization> contractCodes)
    {
        int total = 0;
        foreach (var code in contractCodes)
        {
            total += Rlp.LengthOf(code.ChainId) + Rlp.LengthOf(code.CodeAddress) + Rlp.LengthOf(code.Nonce) + Rlp.LengthOf(code.YParity) + Rlp.LengthOf(code.R) + Rlp.LengthOf(code.S);
        }
        return total;
    }
}
