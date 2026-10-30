// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;

public class BlockRangeUpdateMessageSerializer :
    IZeroInnerMessageSerializer<BlockRangeUpdateMessage>
{
    public void Serialize(IByteBuffer byteBuffer, BlockRangeUpdateMessage message) => this.SerializeWithLengths(byteBuffer, message);

    public void Serialize(IByteBuffer byteBuffer, BlockRangeUpdateMessage message, ref LengthList lengths)
    {
        ByteBufferRlpWriter writer = new(byteBuffer);
        writer.StartSequence(lengths.Read());

        writer.Encode(message.EarliestBlock);
        writer.Encode(message.LatestBlock);
        writer.Encode(message.LatestBlockHash);
    }

    public BlockRangeUpdateMessage Deserialize(IByteBuffer byteBuffer) =>
        byteBuffer.DeserializeRlp(Deserialize);

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

    public void PopulateLength(BlockRangeUpdateMessage message, in LengthListWriter lengths)
    {
        using LengthListWriter sequence = lengths.BeginContainer();
        sequence.Add(
            Rlp.LengthOf(message.EarliestBlock) +
            Rlp.LengthOf(message.LatestBlock) +
            Rlp.LengthOf(message.LatestBlockHash));
    }
}
