// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;
using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Serialization.Rlp.Eip7702;

public class AuthorizationTupleDecoder : IRlpStreamDecoder<AuthorizationTuple>, IRlpValueDecoder<AuthorizationTuple>
{
    public AuthorizationTuple Decode(RlpStream stream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        var chainId = stream.DecodeULong();
        Address? codeAddress = stream.DecodeAddress();
        UInt256?[] nonces = stream.DecodeArray<UInt256?>(s => s.DecodeUInt256());
        if (nonces.Length > 1)
            ThrowInvalidNonceRlpException();
        UInt256? nonce = nonces.Length == 1 ? nonces[0] : null;
        return new AuthorizationTuple(
            chainId,
            codeAddress,
            nonce,
            stream.DecodeULong(),
            stream.DecodeByteArray(),
            stream.DecodeByteArray());
    }

    public AuthorizationTuple Decode(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        var chainId = decoderContext.DecodeULong();
        Address codeAddress = decoderContext.DecodeAddress();

        int nonceLength = decoderContext.ReadSequenceLength();
        //Nonce is optional and is therefore made as a sequence
        UInt256? nonce = null;
        if (nonceLength > 1)
            ThrowInvalidNonceRlpException();
        if (nonceLength == 1)
            nonce = decoderContext.DecodeUInt256();
        return new AuthorizationTuple(
            chainId,
            codeAddress,
            nonce,
            //Signature
            decoderContext.DecodeULong(),
            decoderContext.DecodeByteArray(),
            decoderContext.DecodeByteArray());
    }

    public void Encode(RlpStream stream, AuthorizationTuple item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        throw new NotImplementedException();
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

    public int GetLength(AuthorizationTuple item, RlpBehaviors rlpBehaviors)
    {
        int contentLength = GetContentLength(item);
        return Rlp.LengthOfSequence(contentLength);
    }

    private static int GetContentLength(AuthorizationTuple tuple)
    {
        return  GetContentLengthWithoutSig(tuple.ChainId, tuple.CodeAddress, tuple.Nonce)
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
