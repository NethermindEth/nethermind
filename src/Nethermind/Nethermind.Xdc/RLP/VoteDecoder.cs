// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc.RLP;

public sealed class VoteDecoder : RlpDecoder<Vote>
{
    private static readonly XdcBlockInfoDecoder _xdcBlockInfoDecoder = new();

    protected override Vote DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (decoderContext.TryReadEmptySequence())
        {
            return null;
        }

        RlpReader fields = decoderContext.EnterSequence(out int itemLength);

        BlockRoundInfo proposedBlockInfo = _xdcBlockInfoDecoder.Decode(ref fields, rlpBehaviors);
        Signature signature = null;
        if ((rlpBehaviors & RlpBehaviors.ForSealing) != RlpBehaviors.ForSealing)
        {
            signature = fields.DecodeSignature();
        }
        ulong gapNumber = fields.DecodeULong();

        decoderContext.ExitSequence(in fields, itemLength, rlpBehaviors);
        return new Vote(proposedBlockInfo, gapNumber, signature);
    }

    public override void Encode(ref RlpWriter writer, Vote item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            writer.EncodeNullObject();
            return;
        }
        RlpSequence sequence = writer.StartSequence(GetContentLength(item, rlpBehaviors));
        _xdcBlockInfoDecoder.Encode(ref writer, item.ProposedBlockInfo, rlpBehaviors);
        if ((rlpBehaviors & RlpBehaviors.ForSealing) != RlpBehaviors.ForSealing)
        {
            Span<byte> sigBuffer = stackalloc byte[Signature.Size];
            item.Signature.WriteBytesWithRecoveryTo(sigBuffer);
            writer.Encode(sigBuffer);
        }
        writer.Encode(item.GapNumber);
        writer.EndSequence(sequence);
    }

    public override int GetLength(Vote item, RlpBehaviors rlpBehaviors = RlpBehaviors.None) => Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));

    public int GetContentLength(Vote item, RlpBehaviors rlpBehaviors) => ((rlpBehaviors & RlpBehaviors.ForSealing) != RlpBehaviors.ForSealing ? Rlp.LengthOfSequence(Signature.Size) : 0)
            + Rlp.LengthOf(item.GapNumber)
            + _xdcBlockInfoDecoder.GetLength(item.ProposedBlockInfo, rlpBehaviors);
}
