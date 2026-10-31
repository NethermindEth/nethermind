// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;

public class BlockRangeUpdateMessageSerializer :
    IZeroInnerMessageSerializer<BlockRangeUpdateMessage>
{
    public void Serialize(IByteBuffer byteBuffer, BlockRangeUpdateMessage message)
    {
        int totalLength = GetLength(message, out int contentLength);
        RlpWriter writer = byteBuffer.StartRlpWrite(totalLength);
        RlpSequence sequence = writer.StartSequence(contentLength);

        writer.Encode(message.EarliestBlock);
        writer.Encode(message.LatestBlock);
        writer.Encode(message.LatestBlockHash);

        writer.EndSequence(sequence);
        byteBuffer.CompleteRlpWrite(in writer, totalLength);
    }

    public BlockRangeUpdateMessage Deserialize(IByteBuffer byteBuffer) =>
        byteBuffer.DeserializeRlp(Deserialize);

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

    public int GetLength(BlockRangeUpdateMessage message, out int contentLength)
    {
        contentLength =
            Rlp.LengthOf(message.EarliestBlock) +
            Rlp.LengthOf(message.LatestBlock) +
            Rlp.LengthOf(message.LatestBlockHash);

        return Rlp.LengthOfSequence(contentLength);
    }
}
