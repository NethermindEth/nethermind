// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;
using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;

namespace Nethermind.Serialization.Rlp.Eip7702;

public class AuthorizationTupleDecoder : IRlpStreamDecoder<AuthorizationTuple>, IRlpValueDecoder<AuthorizationTuple>
{
    public AuthorizationTuple Decode(RlpStream stream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int length = stream.ReadSequenceLength();
        int check = length + stream.Position;

        var chainId = stream.DecodeULong();
        Address? codeAddress = stream.DecodeAddress();
        UInt256?[] nonces = stream.DecodeArray<UInt256?>(s => s.DecodeUInt256());

        if (nonces.Length > 1)
            ThrowInvalidNonceRlpException();
        UInt256? nonce = nonces.Length == 1 ? nonces[0] : null;

        ulong yParity = stream.DecodeULong();
        byte[] r = stream.DecodeByteArray();
        byte[] s = stream.DecodeByteArray();
        if (!rlpBehaviors.HasFlag(RlpBehaviors.AllowExtraBytes))
            stream.Check(check);
        return new AuthorizationTuple(
            chainId,
            codeAddress,
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
        UInt256? nonce = null;
        if (nonceLength > 1)
            ThrowInvalidNonceRlpException();
        if (nonceLength == 1)
            nonce = decoderContext.DecodeUInt256();

        ulong yParity = decoderContext.DecodeULong();
        byte[] r = decoderContext.DecodeByteArray();
        byte[] s = decoderContext.DecodeByteArray();
        if (!rlpBehaviors.HasFlag(RlpBehaviors.AllowExtraBytes))
            decoderContext.Check(check);
        return new AuthorizationTuple(
            chainId,
            codeAddress,
            nonce,
            //Signature
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
        if (item.Nonce != null)
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
        RlpStream stream = new RlpStream(totalLength);
        EncodeWithoutSignature(stream, chainId, codeAddress, nonce);
        return stream;
    }

    public void EncodeWithoutSignature(RlpStream stream, ulong chainId, Address codeAddress, UInt256? nonce)
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

    public int GetLength(AuthorizationTuple item, RlpBehaviors rlpBehaviors)
    {
        int contentLength = GetContentLength(item);
        return Rlp.LengthOfSequence(contentLength);
    }

    private static int GetContentLength(AuthorizationTuple tuple)
    {
        return GetContentLengthWithoutSig(tuple.ChainId, tuple.CodeAddress, tuple.Nonce)
            + Rlp.LengthOf(tuple.AuthoritySignature.V)
            + Rlp.LengthOf(tuple.AuthoritySignature.R.AsSpan())
            + Rlp.LengthOf(tuple.AuthoritySignature.S.AsSpan());
    }

    private static int GetContentLengthWithoutSig(ulong chainId, Address codeAddress, UInt256? nonce)
    {
        return
            Rlp.LengthOf(chainId)
            + Rlp.LengthOf(codeAddress)
            + (nonce != null ? Rlp.LengthOfSequence(Rlp.LengthOf(nonce)) : Rlp.OfEmptySequence.Length);
    }

    [DoesNotReturn]
    [StackTraceHidden]
    private static void ThrowInvalidNonceRlpException()
    {
        throw new RlpException("Invalid nonce length in authorization tuple.");
    }
}
