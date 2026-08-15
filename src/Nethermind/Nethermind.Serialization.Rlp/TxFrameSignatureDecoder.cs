// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Extensions;

namespace Nethermind.Serialization.Rlp;

/// <summary>
/// Decodes the EIP-8141 signature tuple <c>[scheme, signer, msg, signature]</c>.
/// An empty signer byte string decodes to null (resolves to the transaction sender).
/// Encoding supports eliding the raw signature bytes of canonical-hash (empty msg) entries,
/// as required by <c>compute_sig_hash</c>.
/// </summary>
public sealed class TxFrameSignatureDecoder : RlpDecoder<TxFrameSignature>
{
    public static readonly TxFrameSignatureDecoder Instance = new();

    private static readonly RlpLimit _msgRlpLimit = RlpLimit.For<TxFrameSignature>(32, nameof(TxFrameSignature.Msg));
    private static readonly RlpLimit _signatureRlpLimit = RlpLimit.For<TxFrameSignature>((int)64.KiB, nameof(TxFrameSignature.Signature));

    protected override TxFrameSignature DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int length = decoderContext.ReadSequenceLength();
        int check = length + decoderContext.Position;

        byte scheme = decoderContext.DecodeByte();
        Address? signer = decoderContext.DecodeAddress();
        ReadOnlyMemory<byte> msg = decoderContext.DecodeByteArrayMemory(_msgRlpLimit);
        ReadOnlyMemory<byte> signature = decoderContext.DecodeByteArrayMemory(_signatureRlpLimit);

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

    public override void Encode<TWriter>(ref TWriter writer, TxFrameSignature item, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        Encode(ref writer, item, elideCanonicalSignatureBytes: false);

    public void Encode<TWriter>(ref TWriter writer, TxFrameSignature item, bool elideCanonicalSignatureBytes)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        bool elide = elideCanonicalSignatureBytes && item.SignsCanonicalHash;
        writer.StartSequence(GetContentLength(item, elide));
        writer.Encode((ulong)item.Scheme);
        writer.Encode(item.Signer);
        writer.Encode(item.Msg);
        if (elide)
        {
            writer.Encode(Bytes.Empty);
        }
        else
        {
            writer.Encode(item.Signature);
        }
    }

    public void EncodeArray<TWriter>(ref TWriter writer, TxFrameSignature[]? items, bool elideCanonicalSignatureBytes)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        if (items is null)
        {
            writer.WriteByte(Rlp.EmptyListByte);
            return;
        }

        writer.StartSequence(GetArrayContentLength(items, elideCanonicalSignatureBytes));
        for (int i = 0; i < items.Length; i++)
        {
            Encode(ref writer, items[i], elideCanonicalSignatureBytes);
        }
    }

    public override int GetLength(TxFrameSignature item, RlpBehaviors rlpBehaviors) =>
        Rlp.LengthOfSequence(GetContentLength(item, elideCanonicalSignatureBytes: false));

    public int GetArrayLength(TxFrameSignature[]? items, bool elideCanonicalSignatureBytes) =>
        items is null ? 1 : Rlp.LengthOfSequence(GetArrayContentLength(items, elideCanonicalSignatureBytes));

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
        + (elideCanonicalSignatureBytes ? 1 : Rlp.LengthOf(item.Signature));

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowInvalidMsgLength() => throw new RlpException("frame signature msg must be empty or a 32-byte digest");
}
