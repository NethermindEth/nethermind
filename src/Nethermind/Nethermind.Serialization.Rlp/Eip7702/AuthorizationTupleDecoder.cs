// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Serialization.Rlp;

public class AuthorizationTupleDecoder : IRlpStreamDecoder<AuthorizationTuple>, IRlpValueDecoder<AuthorizationTuple>
{
    public static readonly AuthorizationTupleDecoder Instance = new();

    public AuthorizationTuple Decode(RlpStream stream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int length = stream.ReadSequenceLength();
        int check = length + stream.Position;
        UInt256 chainId = stream.DecodeUInt256();
        Address? codeAddress = stream.DecodeAddress();
        ulong nonce = stream.DecodeULong();
        byte yParity = stream.DecodeByte();
        UInt256 r = stream.DecodeUInt256();
        UInt256 s = stream.DecodeUInt256();

        if (!rlpBehaviors.HasFlag(RlpBehaviors.AllowExtraBytes))
        {
            stream.Check(check);
        }

        if (codeAddress is null)
        {
            ThrowMissingCodeAddressException();
        }

        return new AuthorizationTuple(chainId, codeAddress, nonce, yParity, r, s);
    }

    public AuthorizationTuple Decode(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int length = decoderContext.ReadSequenceLength();
        int check = length + decoderContext.Position;
        UInt256 chainId = decoderContext.DecodeUInt256();
        Address? codeAddress = decoderContext.DecodeAddress();
        ulong nonce = decoderContext.DecodeULong();
        byte yParity = decoderContext.DecodeByte();
        UInt256 r = decoderContext.DecodeUInt256();
        UInt256 s = decoderContext.DecodeUInt256();

        if (!rlpBehaviors.HasFlag(RlpBehaviors.AllowExtraBytes))
        {
            decoderContext.Check(check);
        }

        if (codeAddress is null)
        {
            ThrowMissingCodeAddressException();
        }

        return new AuthorizationTuple(chainId, codeAddress, nonce, yParity, r, s);
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
        stream.Encode(item.Nonce);
        stream.Encode(item.AuthoritySignature.V - Signature.VOffset);
        stream.Encode(new UInt256(item.AuthoritySignature.R.Span, true));
        stream.Encode(new UInt256(item.AuthoritySignature.S.Span, true));
    }

    public static void EncodeWithoutSignature(RlpStream stream, UInt256 chainId, Address codeAddress, ulong nonce)
    {
        int contentLength = GetContentLengthWithoutSig(chainId, codeAddress, nonce);
        stream.StartSequence(contentLength);
        stream.Encode(chainId);
        stream.Encode(codeAddress ?? throw new RlpException($"Invalid tx {nameof(AuthorizationTuple)} format - address is null"));
        stream.Encode(nonce);
    }

    public int GetLength(AuthorizationTuple item, RlpBehaviors rlpBehaviors) => Rlp.LengthOfSequence(GetContentLength(item));

    private static int GetContentLength(AuthorizationTuple tuple) =>
        GetContentLengthWithoutSig(tuple.ChainId, tuple.CodeAddress, tuple.Nonce)
        + Rlp.LengthOf(tuple.AuthoritySignature.V - Signature.VOffset)
        + Rlp.LengthOf(new UInt256(tuple.AuthoritySignature.R.Span, true))
        + Rlp.LengthOf(new UInt256(tuple.AuthoritySignature.S.Span, true));

    private static int GetContentLengthWithoutSig(UInt256 chainId, Address codeAddress, ulong nonce) =>
        Rlp.LengthOf(chainId)
        + Rlp.LengthOf(codeAddress)
        + Rlp.LengthOf(nonce);

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowMissingCodeAddressException() => throw new RlpException("Missing code address for Authorization");
}
