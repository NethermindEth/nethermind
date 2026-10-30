// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.FluentRlp;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Nethermind.Serialization.Rlp;

[method: DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(AuthorizationTupleDecoder))]
public sealed class AuthorizationTupleDecoder() : RlpDecoder<AuthorizationTuple>
{
    public static readonly AuthorizationTupleDecoder Instance = new();

    protected override AuthorizationTuple DecodeInternal(ref RlpReader reader, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        => (rlpBehaviors & RlpBehaviors.TrustedInput) != 0 ? Decode<OffFlag>(ref reader, rlpBehaviors) : Decode<OnFlag>(ref reader, rlpBehaviors);

    // Not inlined into the list loop of a transaction, so that its reads keep their own inline budget.
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static AuthorizationTuple Decode<TCanonicalChecks>(ref RlpReader reader, RlpBehaviors rlpBehaviors) where TCanonicalChecks : struct, IFlag
    {
        RlpReader fields = reader.EnterSequence<TCanonicalChecks>(out int itemLength);
        UInt256 chainId = fields.DecodeUInt256<TCanonicalChecks>();
        Address codeAddress = fields.DecodeAddress();
        ulong nonce = fields.DecodeULong<TCanonicalChecks>();
        byte yParity = fields.DecodeByte<TCanonicalChecks>();
        UInt256 r = fields.DecodeUInt256<TCanonicalChecks>();
        UInt256 s = fields.DecodeUInt256<TCanonicalChecks>();
        reader.ExitSequence(in fields, itemLength, rlpBehaviors);

        return new AuthorizationTuple(chainId, codeAddress, nonce, yParity, r, s);
    }

    public override void Encode(ref RlpWriter writer, AuthorizationTuple item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        ArgumentNullException.ThrowIfNull(item);
        int contentLength = GetContentLength(item);
        RlpSequence sequence = writer.StartSequence(contentLength);
        writer.Encode(item.ChainId);
        writer.Encode(item.CodeAddress);
        writer.Encode(item.Nonce);
        writer.Encode(item.AuthoritySignature.V - Signature.VOffset);
        writer.Encode(new UInt256(item.AuthoritySignature.R.Span, true));
        writer.Encode(new UInt256(item.AuthoritySignature.S.Span, true));
        writer.EndSequence(sequence);
    }

    public static void EncodeWithoutSignature(ref RlpWriter writer, in UInt256 chainId, Address codeAddress, ulong nonce)
    {
        int contentLength = GetContentLengthWithoutSig(chainId, codeAddress, nonce);
        RlpSequence sequence = writer.StartSequence(contentLength);
        writer.Encode(chainId);
        writer.Encode(codeAddress ?? throw new ArgumentException($"Invalid tx {nameof(AuthorizationTuple)} format - address is null", nameof(codeAddress)));
        writer.Encode(nonce);
        writer.EndSequence(sequence);
    }

    public static void EncodeSignaturePayload(ref RlpWriter writer, in UInt256 chainId, Address codeAddress, ulong nonce)
    {
        writer.WriteByte(Eip7702Constants.Magic);
        EncodeWithoutSignature(ref writer, chainId, codeAddress, nonce);
    }

    /// <summary>Gets the number of bytes that <see cref="EncodeSignaturePayload"/> writes.</summary>
    public static int GetSignaturePayloadLength(in UInt256 chainId, Address codeAddress, ulong nonce)
        => 1 + Rlp.LengthOfSequence(GetContentLengthWithoutSig(chainId, codeAddress, nonce));

    public void EncodeArray(ref RlpWriter writer, AuthorizationTuple[]? items, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (items is null)
        {
            writer.EncodeNullObject();
            return;
        }

        RlpSequence sequence = writer.StartSequence(GetContentLength(items, rlpBehaviors));
        for (int i = 0; i < items.Length; i++)
        {
            AuthorizationTuple? item = items[i];
            if (item is null)
            {
                writer.EncodeNullObject();
            }
            else
            {
                Encode(ref writer, item, rlpBehaviors);
            }
        }

        writer.EndSequence(sequence);
    }

    public override int GetLength(AuthorizationTuple? item, RlpBehaviors rlpBehaviors)
        => Rlp.LengthOfSequence(GetContentLength(item ?? throw new ArgumentNullException(nameof(item))));

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
