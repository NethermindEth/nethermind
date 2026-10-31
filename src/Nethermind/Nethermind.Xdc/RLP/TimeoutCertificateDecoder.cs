// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.Types;
using System;

namespace Nethermind.Xdc.RLP;

public sealed class TimeoutCertificateDecoder : RlpDecoder<TimeoutCertificate>
{
    protected override TimeoutCertificate DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (decoderContext.TryReadEmptySequence())
        {
            return null;
        }
        RlpReader fields = decoderContext.EnterSequence(out int itemLength);

        ulong round = fields.DecodeULong();

        byte[][]? signatureBytes = fields.DecodeByteArrays(innerSize: Signature.Size);
        Signature[]? signatures = null;
        if (signatureBytes is not null)
        {
            signatures = new Signature[signatureBytes.Length];
            for (int i = 0; i < signatures.Length; i++)
            {
                signatures[i] = new Signature(signatureBytes[i].AsSpan(0, 64), signatureBytes[i][64]);
            }
        }

        ulong gapNumber = fields.DecodeULong();

        decoderContext.ExitSequence(in fields, itemLength, rlpBehaviors);

        return new TimeoutCertificate(round, signatures, gapNumber);
    }

    public override void Encode(ref RlpWriter writer, TimeoutCertificate item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            writer.EncodeNullObject();
            return;
        }

        RlpSequence sequence = writer.StartSequence(GetContentLength(item, rlpBehaviors));

        writer.Encode(item.Round);

        if (item.Signatures is null)
            writer.EncodeNullObject();
        else
        {
            RlpSequence signatures = writer.StartSequence(SignaturesLength(item));
            Span<byte> sigBuffer = stackalloc byte[Signature.Size];
            foreach (Signature sig in item.Signatures)
            {
                sig.WriteBytesWithRecoveryTo(sigBuffer);
                writer.Encode(sigBuffer);
            }

            writer.EndSequence(signatures);
        }

        writer.Encode(item.GapNumber);
        writer.EndSequence(sequence);
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
