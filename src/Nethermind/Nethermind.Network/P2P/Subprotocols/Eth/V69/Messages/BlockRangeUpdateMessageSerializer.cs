// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;

public class BlockRangeUpdateMessageSerializer :
    IZeroMessageSerializer<BlockRangeUpdateMessage>
{
    public void Serialize(Span<byte> buffer, BlockRangeUpdateMessage message)
    {
        GetLength(message, out int contentLength);
        RlpWriter writer = new(buffer);
        writer.StartSequence(contentLength);

        writer.Encode(message.EarliestBlock);
        writer.Encode(message.LatestBlock);
        writer.Encode(message.LatestBlockHash);
    }

    public BlockRangeUpdateMessage Deserialize(ReadOnlySpan<byte> data, out int consumed)
    {
        RlpReader ctx = new(data);
        BlockRangeUpdateMessage msg = Deserialize(ref ctx);
        consumed = ctx.Position;
        return msg;
    }

    private static BlockRangeUpdateMessage Deserialize(ref RlpReader ctx)
    {
        ctx.ReadSequenceLength();

        return new BlockRangeUpdateMessage
        {
            EarliestBlock = ctx.DecodeULong(),
            LatestBlock = ctx.DecodeULong(),
            LatestBlockHash = ctx.DecodeKeccak()
        };
    }

    public int GetLength(BlockRangeUpdateMessage message, out int contentLength)
    {
        contentLength =
            Rlp.LengthOf(message.EarliestBlock) +
            Rlp.LengthOf(message.LatestBlock) +
            Rlp.LengthOf(message.LatestBlockHash);

        return Rlp.LengthOfSequence(contentLength);
    }
}
