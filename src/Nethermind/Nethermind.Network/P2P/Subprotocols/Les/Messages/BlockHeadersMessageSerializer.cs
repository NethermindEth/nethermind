// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Les.Messages
{
    public class BlockHeadersMessageSerializer : IZeroMessageSerializer<BlockHeadersMessage>
    {
        public void Serialize(IByteBuffer byteBuffer, BlockHeadersMessage message)
        {
            Eth.V62.Messages.BlockHeadersMessageSerializer ethSerializer = new();
            Rlp ethMessage = new(ethSerializer.Serialize(message.EthMessage));
            int contentLength =
                Rlp.LengthOf(message.RequestId) +
                Rlp.LengthOf(message.BufferValue) +
                ethMessage.Length;

            int totalLength = Rlp.LengthOfSequence(contentLength);

            RlpStream rlpStream = new NettyRlpStream(byteBuffer);
            byteBuffer.EnsureWritable(totalLength, true);

            rlpStream.StartSequence(contentLength);
            rlpStream.Encode(message.RequestId);
            rlpStream.Encode(message.BufferValue);
            rlpStream.Encode(ethMessage);
        }

        public BlockHeadersMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyRlpStream rlpStream = new(byteBuffer);
            return Deserialize(rlpStream);
        }

        private static BlockHeadersMessage Deserialize(RlpStream rlpStream)
        {
            BlockHeadersMessage blockHeadersMessage = new();
            rlpStream.ReadSequenceLength();
            blockHeadersMessage.RequestId = rlpStream.DecodeLong();
            blockHeadersMessage.BufferValue = rlpStream.DecodeInt();
            blockHeadersMessage.EthMessage = Eth.V62.Messages.BlockHeadersMessageSerializer.Deserialize(rlpStream);
            return blockHeadersMessage;
        }
    }
}
