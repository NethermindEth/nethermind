// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Serialization.Rlp;

public class AuthorizationTupleDecoder : IRlpStreamDecoder<AuthorizationTuple>, IRlpValueDecoder<AuthorizationTuple>
{
    public static readonly AuthorizationTupleDecoder Instance = new();

    public AuthorizationTuple Decode(RlpStream stream, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        Decode(ref stream, rlpBehaviors);

    public AuthorizationTuple Decode(ref RlpValueStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        Decode<RlpValueStream>(ref rlpStream, rlpBehaviors);

    public AuthorizationTuple Decode<T>(ref T rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        where T : IRlpStream, allows ref struct
    {
        int length = rlpStream.ReadSequenceLength();
        int check = length + rlpStream.Position;
        ulong chainId = rlpStream.DecodeULong();
        Address? codeAddress = rlpStream.DecodeAddress();
        ulong nonce = rlpStream.DecodeULong();
        byte yParity = rlpStream.DecodeByte();
        UInt256 r = rlpStream.DecodeUInt256();
        UInt256 s = rlpStream.DecodeUInt256();

        if (!rlpBehaviors.HasFlag(RlpBehaviors.AllowExtraBytes))
        {
            rlpStream.Check(check);
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
        + Rlp.LengthOf(tuple.AuthoritySignature.V - Signature.VOffset)
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
