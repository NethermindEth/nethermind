// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;
using System;
using System.Collections.Generic;

namespace Nethermind.Serialization.Rlp.Eip7702;
public class AuthorizationListDecoder : IRlpStreamDecoder<AuthorizationTuple[]?>, IRlpValueDecoder<AuthorizationTuple[]?>
{
    public AuthorizationTuple?[]? Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (rlpStream.IsNextItemNull())
        {
            rlpStream.ReadByte();
            return null;
        }
        return rlpStream.DecodeArray(DecodeAuthorizationTuple);
    }

    private AuthorizationTuple? DecodeAuthorizationTuple(RlpStream stream)
    {
        bool shouldReturnNull = false;
        var chainId = stream.DecodeULong();
        Address? codeAddress = stream.DecodeAddress();
        shouldReturnNull |= codeAddress is null;
        UInt256?[] nonces = stream.DecodeArray<UInt256?>(s => s.DecodeUInt256());
        shouldReturnNull |= nonces.Length > 1;
        UInt256? nonce = nonces.Length == 1 ? nonces[0] : null;
        return shouldReturnNull
            ? null
            : new AuthorizationTuple(chainId, codeAddress, nonce, stream.DecodeULong(), stream.DecodeByteArray(), stream.DecodeByteArray());
    }

    public AuthorizationTuple[]? Decode(
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

        List<AuthorizationTuple> result = new List<AuthorizationTuple>();

        while (decoderContext.Position < check)
        {
            var chainId = decoderContext.DecodeULong();
            Address codeAddress = decoderContext.DecodeAddress();
            int nonceLength = decoderContext.ReadSequenceLength();
            //Nonce is optional and is therefore made as a sequence
            UInt256? nonce = null;
            if (nonceLength > 0)
                nonce = decoderContext.DecodeUInt256();
            result.Add(new AuthorizationTuple(
                chainId,
                codeAddress,
                nonce,
                //Signature
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

    public void Encode(RlpStream stream, AuthorizationTuple[]? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            stream.WriteByte(Rlp.NullObjectByte);
            return;
        }

        int contentLength = GetContentLength(item);
        stream.StartSequence(contentLength);
        foreach (AuthorizationTuple tuple in item)
        {
            stream.Encode(tuple.ChainId);
            stream.Encode(tuple.CodeAddress ?? throw new RlpException($"Invalid tx {nameof(AuthorizationTuple)} format - address is null"));
            if (tuple.Nonce != null)
            {
                stream.StartSequence(Rlp.LengthOf(tuple.Nonce));
                stream.Encode((UInt256)tuple.Nonce);
            }
            else
            {
                stream.StartSequence(0);
            }
            stream.Encode(tuple.AuthoritySignature.V);
            stream.Encode(tuple.AuthoritySignature.R);
            stream.Encode(tuple.AuthoritySignature.S);
        }
    }

    public RlpStream EncodeForCommitMessage(ulong chainId, Address codeAddress, UInt256? nonce)
    {
        int contentLength = Rlp.LengthOf(chainId) + Rlp.LengthOf(codeAddress) + (nonce != null ? Rlp.LengthOfSequence(Rlp.LengthOf(nonce)) : Rlp.OfEmptySequence.Length);
        var totalLength = Rlp.LengthOfSequence(contentLength);
        RlpStream stream = new RlpStream(totalLength);
        EncodeForCommitMessage(stream, chainId, codeAddress, nonce);
        return stream;
    }

    public void EncodeForCommitMessage(RlpStream stream, ulong chainId, Address codeAddress, UInt256? nonce)
    {
        int contentLength = GetContentLengthWithoutSig(chainId, codeAddress, nonce);
        stream.StartSequence(contentLength);
        stream.Encode(chainId);
        stream.Encode(codeAddress ?? throw new RlpException($"Invalid tx {nameof(AuthorizationTuple)} format - address is null"));
        if (nonce != null)
        {
            stream.StartSequence(Rlp.LengthOf(nonce));
            stream.Encode((UInt256)nonce);
        }
        else
        {
            stream.StartSequence(0);
        }
    }

    public int GetLength(AuthorizationTuple[]? setCodeAuths, RlpBehaviors rlpBehaviors)
    {
        if (setCodeAuths is null)
        {
            return Rlp.OfEmptySequence.Length;
        }

        int contentLength = GetContentLength(setCodeAuths);
        return Rlp.LengthOfSequence(contentLength);
    }

    private static int GetContentLengthWithoutSig(ulong chainId, Address codeAddress, UInt256? nonce)
    {
        return
            Rlp.LengthOf(chainId)
            + Rlp.LengthOf(codeAddress)
            + (nonce != null ? Rlp.LengthOfSequence(Rlp.LengthOf(nonce)) : Rlp.OfEmptySequence.Length);
    }
    private static int GetContentLength(ReadOnlySpan<AuthorizationTuple> setCodeAuths)
    {
        int total = 0;
        foreach (var code in setCodeAuths)
        {
            total += GetContentLengthWithoutSig(code.ChainId, code.CodeAddress, code.Nonce) + Rlp.LengthOf(code.AuthoritySignature.V) + Rlp.LengthOf(code.AuthoritySignature.R.AsSpan()) + Rlp.LengthOf(code.AuthoritySignature.S.AsSpan());
        }
        return total;
    }
}
