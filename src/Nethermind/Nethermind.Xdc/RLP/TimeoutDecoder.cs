// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc.RLP;

public sealed class TimeoutDecoder : RlpDecoder<Timeout>
{
    protected override Timeout DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (decoderContext.TryReadEmptySequence())
        {
            return null;
        }
        RlpReader fields = decoderContext.EnterSequence(out int itemLength);

        ulong round = fields.DecodeULong();

        Signature signature = null;
        if ((rlpBehaviors & RlpBehaviors.ForSealing) != RlpBehaviors.ForSealing)
        {
            signature = fields.DecodeSignature();
        }

        ulong gapNumber = fields.DecodeULong();

        decoderContext.ExitSequence(in fields, itemLength, rlpBehaviors);

        return new Timeout(round, signature, gapNumber);
    }

    public override void Encode(ref RlpWriter writer, Timeout item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            writer.EncodeNullObject();
            return;
        }

        RlpSequence sequence = writer.StartSequence(GetContentLength(item, rlpBehaviors));

        writer.Encode(item.Round);

        // When encoding for sealing, signature is not included
        if ((rlpBehaviors & RlpBehaviors.ForSealing) != RlpBehaviors.ForSealing)
        {
            if (item.Signature is null)
                writer.EncodeNullObject();
            else
            {
                Span<byte> sigBuffer = stackalloc byte[Signature.Size];
                item.Signature.WriteBytesWithRecoveryTo(sigBuffer);
                writer.Encode(sigBuffer);
            }
        }

        writer.Encode(item.GapNumber);
        writer.EndSequence(sequence);
    }

    public override int GetLength(Timeout item, RlpBehaviors rlpBehaviors = RlpBehaviors.None) => Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));
    public int GetContentLength(Timeout? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
            return 0;
        int contentLength = Rlp.LengthOf(item.Round);
        if ((rlpBehaviors & RlpBehaviors.ForSealing) != RlpBehaviors.ForSealing)
        {
            contentLength += Rlp.LengthOfSequence(Signature.Size);
        }
        contentLength += Rlp.LengthOf(item.GapNumber);
        return contentLength;
    }
}
