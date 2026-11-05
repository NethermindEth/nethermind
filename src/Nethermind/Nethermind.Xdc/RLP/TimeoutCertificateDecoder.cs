// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.Types;
using System;

namespace Nethermind.Xdc.RLP;

public sealed class TimeoutCertificateDecoder : RlpDecoder<TimeoutCertificate>
{
    protected override TimeoutCertificate DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (decoderContext.IsNextItemEmptyList())
        {
            decoderContext.ReadByte();
            return null;
        }
        int sequenceLength = decoderContext.ReadSequenceLength();
        int endPosition = decoderContext.Position + sequenceLength;

        ulong round = decoderContext.DecodeULong();

        byte[][]? signatureBytes = decoderContext.DecodeByteArrays(innerSize: Signature.Size);
        Signature[]? signatures = null;
        if (signatureBytes is not null)
        {
            signatures = new Signature[signatureBytes.Length];
            for (int i = 0; i < signatures.Length; i++)
            {
                signatures[i] = new Signature(signatureBytes[i].AsSpan(0, 64), signatureBytes[i][64]);
            }
        }

        ulong gapNumber = decoderContext.DecodeULong();

        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
        {
            decoderContext.Check(endPosition);
        }

        return new TimeoutCertificate(round, signatures, gapNumber);
    }

    public override Rlp Encode(TimeoutCertificate item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
            return Rlp.OfEmptyList;

        byte[] bytes = new byte[GetLength(item, rlpBehaviors)];
        RlpWriter writer = new(bytes);
        Encode(ref writer, item, rlpBehaviors);

        return new Rlp(bytes);
    }

    public override void Encode<TWriter>(ref TWriter writer, TimeoutCertificate item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            writer.EncodeNullObject();
            return;
        }

        writer.StartSequence(GetContentLength(item, rlpBehaviors));

        writer.Encode(item.Round);

        if (item.Signatures is null)
            writer.EncodeNullObject();
        else
        {
            writer.StartSequence(SignaturesLength(item));
            Span<byte> sigBuffer = stackalloc byte[Signature.Size];
            foreach (Signature sig in item.Signatures)
            {
                sig.WriteBytesWithRecoveryTo(sigBuffer);
                writer.Encode(sigBuffer);
            }
        }

        writer.Encode(item.GapNumber);
    }

    public override int GetLength(TimeoutCertificate item, RlpBehaviors rlpBehaviors = RlpBehaviors.None) => Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));
    private int GetContentLength(TimeoutCertificate? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
            return 0;

        return Rlp.LengthOf(item.Round)
               + Rlp.LengthOfSequence(SignaturesLength(item))
               + Rlp.LengthOf(item.GapNumber);
    }

    private static int SignaturesLength(TimeoutCertificate item) => item.Signatures is not null ? item.Signatures.Length * Rlp.LengthOfSequence(Signature.Size) : 0;
}
