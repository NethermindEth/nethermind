// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc.RLP;

internal sealed class XdcBlockInfoDecoder : RlpDecoder<BlockRoundInfo>
{
    protected override BlockRoundInfo DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (decoderContext.TryReadEmptySequence())
        {
            return null;
        }
        RlpReader fields = decoderContext.EnterSequence(out int itemLength);

        byte[] hashBytes = fields.DecodeByteArray();
        if (hashBytes.Length > Hash256.Size)
            throw new RlpReaderException($"Hash length {hashBytes.Length} is longer than max size of 32.");
        ulong round = fields.DecodeULong();
        ulong number = fields.DecodeULong();
        decoderContext.ExitSequenceIgnoringRest(in fields, itemLength);

        return new BlockRoundInfo(new Hash256(hashBytes), round, number);
    }

    public override void Encode(ref RlpWriter writer, BlockRoundInfo item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            writer.EncodeNullObject();
            return;
        }
        RlpSequence sequence = writer.StartSequence(GetContentLength(item, rlpBehaviors));
        writer.Encode(item.Hash);
        writer.Encode(item.Round);
        writer.Encode(item.BlockNumber);
        writer.EndSequence(sequence);
    }

    public override int GetLength(BlockRoundInfo item, RlpBehaviors rlpBehaviors = RlpBehaviors.None) => Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));

    private static int GetContentLength(BlockRoundInfo? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
            return 0;
        return Rlp.LengthOf(item.Hash) +
               Rlp.LengthOf(item.Round) +
               Rlp.LengthOf(item.BlockNumber);
    }

}
