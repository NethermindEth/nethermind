// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;

public class BlockRangeUpdateMessageSerializer :
    IZeroInnerMessageSerializer<BlockRangeUpdateMessage>
{
    public void Serialize(IByteBuffer byteBuffer, BlockRangeUpdateMessage message)
    {
        int totalLength = GetLength(message, out int contentLength);
        byteBuffer.EnsureWritable(totalLength);
        ByteBufferRlpWriter writer = new(byteBuffer);
        writer.StartSequence(contentLength);

        writer.Encode(message.EarliestBlock);
        writer.Encode(message.LatestBlock);
        writer.Encode(message.LatestBlockHash);
    }

    public BlockRangeUpdateMessage Deserialize(IByteBuffer byteBuffer) =>
        byteBuffer.DeserializeRlp(Deserialize) ?? throw new RlpException("Block range update message decoding returned null.");

    private static BlockRangeUpdateMessage Deserialize(ref RlpReader ctx)
    {
        ctx.ReadSequenceLength();

        return new BlockRangeUpdateMessage
        {
            EarliestBlock = ctx.DecodeULong(),
            LatestBlock = ctx.DecodeULong(),
            LatestBlockHash = ctx.DecodeKeccakNonNull()
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
