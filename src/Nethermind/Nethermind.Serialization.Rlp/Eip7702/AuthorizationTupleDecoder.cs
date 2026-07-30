// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using System.Diagnostics;
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

    public override void Encode<TWriter>(ref TWriter writer, AuthorizationTuple item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int contentLength = GetContentLength(item);
        writer.StartSequence(contentLength);
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

    public void EncodeArray<TWriter>(ref TWriter writer, AuthorizationTuple[]? items, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        if (items is null)
        {
            writer.WriteByte(Rlp.EmptyListByte);
            return;
        }

        writer.StartSequence(GetContentLength(items, rlpBehaviors));
        for (int i = 0; i < items.Length; i++)
        {
            AuthorizationTuple? item = items[i];
            if (item is null)
            {
                writer.WriteByte(Rlp.EmptyListByte);
            }
            else
            {
                Encode(ref writer, item, rlpBehaviors);
            }
        }
    }

    public override int GetLength(AuthorizationTuple item, RlpBehaviors rlpBehaviors) => Rlp.LengthOfSequence(GetContentLength(item));

    private static int GetContentLength(AuthorizationTuple tuple) =>
        GetContentLengthWithoutSig(tuple.ChainId, tuple.CodeAddress, tuple.Nonce)
        + Rlp.LengthOf(tuple.AuthoritySignature.V - Signature.VOffset)
        + Rlp.LengthOf(new UInt256(tuple.AuthoritySignature.R.Span, true))
        + Rlp.LengthOf(new UInt256(tuple.AuthoritySignature.S.Span, true));

    private static int GetContentLengthWithoutSig(in UInt256 chainId, Address codeAddress, ulong nonce) =>
        Rlp.LengthOf(chainId)
        + Rlp.LengthOf(codeAddress)
        + Rlp.LengthOf(nonce);

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowMissingCodeAddressException() => throw new RlpException("Missing code address for Authorization");
}
