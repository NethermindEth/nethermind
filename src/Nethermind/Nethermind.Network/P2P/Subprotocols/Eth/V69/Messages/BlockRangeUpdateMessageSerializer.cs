// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;

public class BlockRangeUpdateMessageSerializer :
    IZeroInnerMessageSerializer<BlockRangeUpdateMessage>
{
    public void Serialize(IByteBuffer byteBuffer, BlockRangeUpdateMessage message)
    {
        NettyRlpStream rlpStream = new(byteBuffer);

        int totalLength = GetLength(message, out int contentLength);
        byteBuffer.EnsureWritable(totalLength);
        rlpStream.StartSequence(contentLength);

        rlpStream.Encode(message.EarliestBlock);
        rlpStream.Encode(message.LatestBlock);
        rlpStream.Encode(message.LatestBlockHash);
    }

    public BlockRangeUpdateMessage Deserialize(IByteBuffer byteBuffer) =>
        byteBuffer.DeserializeRlp(Deserialize);

    private static BlockRangeUpdateMessage Deserialize(ref Rlp.ValueDecoderContext ctx)
    {
        ctx.ReadSequenceLength();

        return new BlockRangeUpdateMessage
        {
            EarliestBlock = ctx.DecodeLong(),
            LatestBlock = ctx.DecodeLong(),
            LatestBlockHash = ctx.DecodeKeccak() ?? Hash256.Zero
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
