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
public class TxContractCodeDecoder : IRlpStreamDecoder<TxContractCode[]?>, IRlpValueDecoder<TxContractCode[]?>
{
    public TxContractCode[]? Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (rlpStream.IsNextItemNull())
        {
            rlpStream.ReadByte();
            return null;
        }

        int outerLength = rlpStream.ReadSequenceLength();

        int check = rlpStream.Position + outerLength;

        List<TxContractCode> result = new List<TxContractCode>();

        while (rlpStream.Position < check)
        {
            //TODO check what is valid for the fields here
            byte[] contractCode = rlpStream.DecodeByteArray();
            UInt256 yParity = rlpStream.DecodeUInt256();
            UInt256 r = rlpStream.DecodeUInt256();
            UInt256 s = rlpStream.DecodeUInt256();
            result.Add(new TxContractCode(contractCode, yParity, r, s));
        }

        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
        {
            rlpStream.Check(check);
        }
        return result.ToArray();
    }

    public TxContractCode[]? Decode(
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

        List<TxContractCode> result = new List<TxContractCode>();

        while (decoderContext.Position < check)
        {
            //TODO check what is valid for the fields here
            byte[] contractCode = decoderContext.DecodeByteArray();
            UInt256 yParity = decoderContext.DecodeUInt256();
            UInt256 r = decoderContext.DecodeUInt256();
            UInt256 s = decoderContext.DecodeUInt256();
            result.Add(new TxContractCode(contractCode, yParity, r, s));
        }

        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
        {
            decoderContext.Check(check);
        }
        return result.ToArray();
    }

    public void Encode(RlpStream stream, TxContractCode[]? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            stream.WriteByte(Rlp.NullObjectByte);
            return;
        }

        int contentLength = GetContentLength(item);
        stream.StartSequence(contentLength);
        foreach (TxContractCode contractCode in item)
        {
            if (contractCode.ContractCode == null)
            {
                stream.WriteByte(Rlp.NullObjectByte);
            }
            else
            {
                stream.Encode(contractCode.ContractCode);
            }
            stream.Encode(contractCode.YParity);
            stream.Encode(contractCode.R);
            stream.Encode(contractCode.S);
        }
    }

    public int GetLength(TxContractCode[]? contractCodes, RlpBehaviors rlpBehaviors)
    {
        if (contractCodes is null)
        {
            return Rlp.OfEmptySequence.Length;
        }

        int contentLength = GetContentLength(contractCodes);
        return Rlp.LengthOfSequence(contentLength);
    }

    private static int GetContentLength(ReadOnlySpan<TxContractCode> contractCodes)
    {
        int total = 0;
        foreach (var code in contractCodes)
        {
            total += Rlp.LengthOf(code.ContractCode) + Rlp.LengthOf(code.YParity) + Rlp.LengthOf(code.R) + Rlp.LengthOf(code.S);
        }
        return total;
    }
}
