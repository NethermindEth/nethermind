// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;

namespace Nethermind.Serialization.Rlp;

public class AuthorizationTupleDecoder : IRlpStreamDecoder<AuthorizationTuple>, IRlpValueDecoder<AuthorizationTuple>
{
    public AuthorizationTuple Decode(RlpStream stream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int length = stream.ReadSequenceLength();
        int check = length + stream.Position;

        var chainId = stream.DecodeULong();
        Address? codeAddress = stream.DecodeAddress();
        UInt256?[] nonces = stream.DecodeArray<UInt256?>(s => s.DecodeUInt256());

        UInt256? nonce = nonces.Length switch
        {
            0 => null,
            1 => nonces[0],
            _ => ThrowInvalidNonceRlpException()
        };

        ulong yParity = stream.DecodeULong();
        byte[] r = stream.DecodeByteArray();
        byte[] s = stream.DecodeByteArray();
        if (!rlpBehaviors.HasFlag(RlpBehaviors.AllowExtraBytes))
            stream.Check(check);
        return new AuthorizationTuple(
            chainId,
            codeAddress!,
            nonce,
            yParity,
            r,
            s);
    }

    public AuthorizationTuple Decode(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int length = decoderContext.ReadSequenceLength();
        int check = length + decoderContext.Position;

        var chainId = decoderContext.DecodeULong();
        Address codeAddress = decoderContext.DecodeAddress();

        int nonceLength = decoderContext.ReadSequenceLength();
        //Nonce is optional and is therefore made as a sequence
        UInt256? nonce = nonceLength switch
        {
            0 => null,
            1 => decoderContext.DecodeUInt256(),
            _ => ThrowInvalidNonceRlpException()
        };

        ulong yParity = decoderContext.DecodeULong();
        byte[] r = decoderContext.DecodeByteArray();
        byte[] s = decoderContext.DecodeByteArray();
        if (!rlpBehaviors.HasFlag(RlpBehaviors.AllowExtraBytes))
            decoderContext.Check(check);
        return new AuthorizationTuple(
            chainId,
            codeAddress!,
            nonce,
            yParity,
            r,
            s);
    }

    public RlpStream Encode(AuthorizationTuple item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        RlpStream stream = new(GetLength(item, rlpBehaviors));
        Encode(stream, item, rlpBehaviors);
        return stream;
    }

    public void Encode(RlpStream stream, AuthorizationTuple item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int contentLength = GetContentLength(item);
        stream.StartSequence(contentLength);
        stream.Encode(item.ChainId);
        stream.Encode(item.CodeAddress);
        if (item.Nonce is not null)
        {
            stream.StartSequence(Rlp.LengthOf(item.Nonce));
            stream.Encode((UInt256)item.Nonce);
        }
        else
        {
            stream.StartSequence(0);
        }
        stream.Encode(item.AuthoritySignature.RecoveryId);
        stream.Encode(item.AuthoritySignature.R);
        stream.Encode(item.AuthoritySignature.S);
    }

    public RlpStream EncodeWithoutSignature(ulong chainId, Address codeAddress, UInt256? nonce)
    {
        int contentLength = GetContentLengthWithoutSig(chainId, codeAddress, nonce);
        var totalLength = Rlp.LengthOfSequence(contentLength);
        RlpStream stream = new(totalLength);
        EncodeWithoutSignature(stream, chainId, codeAddress, nonce);
        return stream;
    }

    public void EncodeWithoutSignature(RlpStream stream, ulong chainId, Address codeAddress, UInt256? nonce)
    {
        int contentLength = GetContentLengthWithoutSig(chainId, codeAddress, nonce);
        stream.StartSequence(contentLength);
        stream.Encode(chainId);
        stream.Encode(codeAddress ?? throw new RlpException($"Invalid tx {nameof(AuthorizationTuple)} format - address is null"));
        if (nonce is not null)
        {
            stream.StartSequence(Rlp.LengthOf(nonce));
            stream.Encode((UInt256)nonce);
        }
        else
        {
            stream.StartSequence(0);
        }
    }

    public int GetLength(AuthorizationTuple item, RlpBehaviors rlpBehaviors) => Rlp.LengthOfSequence(GetContentLength(item));

    private static int GetContentLength(AuthorizationTuple tuple) =>
        GetContentLengthWithoutSig(tuple.ChainId, tuple.CodeAddress, tuple.Nonce)
        + Rlp.LengthOf(tuple.AuthoritySignature.RecoveryId)
        + Rlp.LengthOf(tuple.AuthoritySignature.R.AsSpan())
        + Rlp.LengthOf(tuple.AuthoritySignature.S.AsSpan());

    private static int GetContentLengthWithoutSig(ulong chainId, Address codeAddress, UInt256? nonce) =>
        Rlp.LengthOf(chainId)
        + Rlp.LengthOf(codeAddress)
        + (nonce is not null ? Rlp.LengthOfSequence(Rlp.LengthOf(nonce)) : Rlp.OfEmptySequence.Length);

    [DoesNotReturn]
    [StackTraceHidden]
    private static UInt256 ThrowInvalidNonceRlpException() =>
        throw new RlpException("Invalid nonce length in authorization tuple.");
}
