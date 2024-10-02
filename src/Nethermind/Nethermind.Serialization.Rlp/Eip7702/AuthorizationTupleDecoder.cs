// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp.Eip2930;
using System;
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
        ulong chainId = stream.DecodeULong();
        Address? codeAddress = stream.DecodeAddress();
        ulong nonce = stream.DecodeULong();
        ulong yParity = stream.DecodeULong();
        byte[] r = stream.DecodeByteArray();
        byte[] s = stream.DecodeByteArray();

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
        ulong chainId = decoderContext.DecodeULong();
        Address? codeAddress = decoderContext.DecodeAddress();
        ulong nonce = decoderContext.DecodeULong();
        ulong yParity = decoderContext.DecodeULong();
        byte[] r = decoderContext.DecodeByteArray();
        byte[] s = decoderContext.DecodeByteArray();

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
        stream.Encode(item.AuthoritySignature.RecoveryId);
        stream.Encode(new UInt256(item.AuthoritySignature.R, true));
        stream.Encode(new UInt256(item.AuthoritySignature.S, true));
    }

    public NettyRlpStream EncodeWithoutSignature(ulong chainId, Address codeAddress, ulong nonce)
    {
        int contentLength = GetContentLengthWithoutSig(chainId, codeAddress, nonce);
        var totalLength = Rlp.LengthOfSequence(contentLength);
        IByteBuffer byteBuffer = PooledByteBufferAllocator.Default.Buffer(totalLength);
        NettyRlpStream stream = new(byteBuffer);
        EncodeWithoutSignature(stream, chainId, codeAddress, nonce);
        return stream;
    }

    public void EncodeWithoutSignature(RlpStream stream, ulong chainId, Address codeAddress, ulong nonce)
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
        + Rlp.LengthOf(tuple.AuthoritySignature.RecoveryId)
        + Rlp.LengthOf(new UInt256(tuple.AuthoritySignature.R.AsSpan(), true))
        + Rlp.LengthOf(new UInt256(tuple.AuthoritySignature.S.AsSpan(), true));

    private static int GetContentLengthWithoutSig(ulong chainId, Address codeAddress, ulong nonce) =>
        Rlp.LengthOf(chainId)
        + Rlp.LengthOf(codeAddress)
        + Rlp.LengthOf(nonce);

    [DoesNotReturn]
    [StackTraceHidden]
    private static void ThrowMissingCodeAddressException() => throw new RlpException("Missing code address for Authorization");
}
