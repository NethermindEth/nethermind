// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;

namespace Nethermind.Serialization.Rlp;

/// <summary>Decodes the EIP-8141 signature tuple <c>[scheme, signer, msg, signature]</c>; an empty signer decodes to
/// null. Encoding can replace the signature bytes of canonical-hash (empty msg) entries with their length, as
/// <c>compute_sig_hash</c> needs.</summary>
public sealed class TxFrameSignatureDecoder : RlpDecoder<TxFrameSignature>
{
    public static readonly TxFrameSignatureDecoder Instance = new();

    private static readonly RlpLimit _msgRlpLimit = RlpLimit.For<TxFrameSignature>(32, nameof(TxFrameSignature.Msg));

    // Only the limit varies, and it is read once per signature, so the names are built once here. MaxBlockGas
    // cannot be captured with them: configuration sets it after type initialization may have run.
    private static readonly RlpLimit _signatureRlpLimitShape = RlpLimit.For<TxFrameSignature>(0, nameof(TxFrameSignature.Signature));

    // The spec bounds the signature bytes only through gas: they are charged as calldata, so no block can pay
    // for a longer one than this.
    private static RlpLimit SignatureRlpLimit => _signatureRlpLimitShape with
    {
        Limit = (int)Math.Min(RlpLimit.MaxBlockGas / GasCostOf.TxDataZero + 1, int.MaxValue)
    };

    protected override TxFrameSignature DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int length = decoderContext.ReadSequenceLength();
        int check = length + decoderContext.Position;

        byte scheme = decoderContext.DecodeByte();
        Address? signer = decoderContext.DecodeAddressOrNull();
        ReadOnlyMemory<byte> msg = decoderContext.DecodeByteArrayMemory(_msgRlpLimit);
        ReadOnlyMemory<byte> signature = decoderContext.DecodeByteArrayMemory(SignatureRlpLimit);

        if (!rlpBehaviors.HasFlag(RlpBehaviors.AllowExtraBytes))
        {
            decoderContext.Check(check);
        }

        if (msg.Length != 0 && msg.Length != 32)
        {
            ThrowInvalidMsgLength();
        }

        return new TxFrameSignature(scheme, signer, msg, signature);
    }

    public override void Encode<TWriter>(ref TWriter writer, TxFrameSignature item, ref LengthStack lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        writer.StartSequence(lengths.Pop());
        EncodeContent(ref writer, item, elide: false);
    }

    private static void EncodeContent<TWriter>(ref TWriter writer, TxFrameSignature item, bool elide)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        writer.Encode((ulong)item.Scheme);
        writer.Encode(item.Signer);
        writer.Encode(item.Msg);
        if (elide)
        {
            writer.Encode((ulong)item.Signature.Length);
        }
        else
        {
            writer.Encode(item.Signature);
        }
    }

    /// <summary>Writes a sequence populated by <see cref="PopulateArrayLength"/>.</summary>
    public void EncodeArray<TWriter>(ref TWriter writer, TxFrameSignature[]? items, ref LengthStack lengths, bool elideCanonicalSignatureBytes)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        writer.StartSequence(lengths.Pop());
        if (items is null) return;

        for (int i = 0; i < items.Length; i++)
        {
            TxFrameSignature item = items[i];
            bool elide = elideCanonicalSignatureBytes && item.SignsCanonicalHash;
            writer.StartSequence(GetContentLength(item, elide));
            EncodeContent(ref writer, item, elide);
        }
    }

    public override int PopulateLength(TxFrameSignature item, ref LengthStack lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int contentLength = GetContentLength(item, elideCanonicalSignatureBytes: false);
        lengths.Push(contentLength);
        return Rlp.LengthOfSequence(contentLength);
    }

    /// <summary>Pushes the content length of the sequence of <paramref name="items"/>, a <see langword="null"/> one taking an empty list, and returns its total.</summary>
    public int PopulateArrayLength(TxFrameSignature[]? items, ref LengthStack lengths, bool elideCanonicalSignatureBytes)
    {
        int contentLength = items is null ? 0 : GetArrayContentLength(items, elideCanonicalSignatureBytes);
        lengths.Push(contentLength);
        return Rlp.LengthOfSequence(contentLength);
    }

    private static int GetArrayContentLength(TxFrameSignature[] items, bool elideCanonicalSignatureBytes)
    {
        int length = 0;
        for (int i = 0; i < items.Length; i++)
        {
            TxFrameSignature item = items[i];
            bool elide = elideCanonicalSignatureBytes && item.SignsCanonicalHash;
            length += Rlp.LengthOfSequence(GetContentLength(item, elide));
        }

        return length;
    }

    private static int GetContentLength(TxFrameSignature item, bool elideCanonicalSignatureBytes) =>
        Rlp.LengthOf((ulong)item.Scheme)
        + Rlp.LengthOf(item.Signer)
        + Rlp.LengthOf(item.Msg)
        + (elideCanonicalSignatureBytes ? Rlp.LengthOf((ulong)item.Signature.Length) : Rlp.LengthOf(item.Signature));

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowInvalidMsgLength() => throw new RlpException("frame signature msg must be empty or a 32-byte digest");
}
