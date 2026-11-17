// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;

public class BlockRangeUpdateMessageSerializer :
    IMessageSerializer<BlockRangeUpdateMessage>
{
    public int Serialize(Span<byte> destination, BlockRangeUpdateMessage message)
    {
        RlpWriter writer = RlpWriter.ContentWriter(destination);
        RlpSequence sequence = writer.StartSequence(GetContentLength(message));

        writer.Encode(message.EarliestBlock);
        writer.Encode(message.LatestBlock);
        writer.Encode(message.LatestBlockHash);
        writer.EndSequence(sequence);
        return writer.Length;
    }

    public BlockRangeUpdateMessage Deserialize(ReadOnlySpan<byte> data)
    {
        RlpReader ctx = new(data);
        BlockRangeUpdateMessage message = Deserialize(ref ctx);
        ctx.CheckEnd();
        return message;
    }

    private static BlockRangeUpdateMessage Deserialize(ref RlpReader reader)
    {
        RlpReader ctx = reader.EnterSequence(out int itemLength);

        BlockRangeUpdateMessage message = new()
        {
            EarliestBlock = ctx.DecodeULong(),
            LatestBlock = ctx.DecodeULong(),
            LatestBlockHash = ctx.DecodeKeccak()
        };
        reader.ExitSequenceIgnoringRest(ctx, itemLength);
        return message;
    }

    public int GetLength(BlockRangeUpdateMessage message) => Rlp.LengthOfSequence(GetContentLength(message));

    private static int GetContentLength(BlockRangeUpdateMessage message) =>
        Rlp.LengthOf(message.EarliestBlock) +
        Rlp.LengthOf(message.LatestBlock) +
        Rlp.LengthOf(message.LatestBlockHash);
}
