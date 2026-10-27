// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using System;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Serialization.Rlp;

[method: DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(AuthorizationTupleDecoder))]
public sealed class AuthorizationTupleDecoder() : RlpDecoder<AuthorizationTuple>
{
    public static readonly AuthorizationTupleDecoder Instance = new();

    protected override AuthorizationTuple DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int length = decoderContext.ReadSequenceLength();
        int check = length + decoderContext.Position;
        UInt256 chainId = decoderContext.DecodeUInt256();
        Address codeAddress = decoderContext.DecodeAddress();
        ulong nonce = decoderContext.DecodeULong();
        byte yParity = decoderContext.DecodeByte();
        UInt256 r = decoderContext.DecodeUInt256();
        UInt256 s = decoderContext.DecodeUInt256();

        if (!rlpBehaviors.HasFlag(RlpBehaviors.AllowExtraBytes))
        {
            decoderContext.Check(check);
        }

        return new AuthorizationTuple(chainId, codeAddress, nonce, yParity, r, s);
    }

    public override void PopulateLength(AuthorizationTuple item, ref LengthStack lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int contentLength = GetContentLength(item ?? throw new ArgumentNullException(nameof(item)));
        lengths.Push(contentLength);
        lengths.Push(Rlp.LengthOfSequence(contentLength));
    }

    public override void Encode<TWriter>(AuthorizationTuple item, ref LengthStack lengths, ref TWriter writer, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        ArgumentNullException.ThrowIfNull(item);
        writer.StartSequence(lengths.Pop());
        writer.Encode(item.ChainId);
        writer.Encode(item.CodeAddress);
        writer.Encode(item.Nonce);
        writer.Encode(item.AuthoritySignature.V - Signature.VOffset);
        writer.Encode(new UInt256(item.AuthoritySignature.R.Span, true));
        writer.Encode(new UInt256(item.AuthoritySignature.S.Span, true));
    }

    public static void EncodeWithoutSignature<TWriter>(ref TWriter writer, in UInt256 chainId, Address codeAddress, ulong nonce)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        int contentLength = GetContentLengthWithoutSig(chainId, codeAddress, nonce);
        writer.StartSequence(contentLength);
        writer.Encode(chainId);
        writer.Encode(codeAddress ?? throw new RlpException($"Invalid tx {nameof(AuthorizationTuple)} format - address is null"));
        writer.Encode(nonce);
    }

    public static void EncodeSignaturePayload<TWriter>(ref TWriter writer, in UInt256 chainId, Address codeAddress, ulong nonce)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        writer.WriteByte(Eip7702Constants.Magic);
        EncodeWithoutSignature(ref writer, chainId, codeAddress, nonce);
    }

    private static int GetContentLength(AuthorizationTuple tuple) =>
        GetContentLengthWithoutSig(tuple.ChainId, tuple.CodeAddress, tuple.Nonce)
        + Rlp.LengthOf(tuple.AuthoritySignature.V - Signature.VOffset)
        + Rlp.LengthOf(new UInt256(tuple.AuthoritySignature.R.Span, true))
        + Rlp.LengthOf(new UInt256(tuple.AuthoritySignature.S.Span, true));

    private static int GetContentLengthWithoutSig(in UInt256 chainId, Address codeAddress, ulong nonce) =>
        Rlp.LengthOf(chainId)
        + Rlp.LengthOf(codeAddress)
        + Rlp.LengthOf(nonce);
}
