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

    public BlockRangeUpdateMessage Deserialize(IByteBuffer byteBuffer)
    {
        RlpStream rlpStream = new NettyRlpStream(byteBuffer);
        rlpStream.ReadSequenceLength();

        BlockRangeUpdateMessage statusMessage = new()
        {
            EarliestBlock = rlpStream.DecodeLong(),
            LatestBlock = rlpStream.DecodeLong(),
            LatestBlockHash = rlpStream.DecodeKeccak() ?? Hash256.Zero
        };

        return statusMessage;
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
