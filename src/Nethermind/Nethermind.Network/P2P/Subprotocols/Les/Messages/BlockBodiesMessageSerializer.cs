// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Les.Messages
{
    public class BlockBodiesMessageSerializer : IZeroMessageSerializer<BlockBodiesMessage>
    {
        public void Serialize(IByteBuffer byteBuffer, BlockBodiesMessage message)
        {
            Eth.V62.Messages.BlockBodiesMessageSerializer ethSerializer = new();
            int ethMessageTotalLength = ethSerializer.GetLength(message.EthMessage, out int ethMessageContentLength);
            int contentLength = Rlp.LengthOf(message.RequestId) + Rlp.LengthOf(message.BufferValue) + ethMessageTotalLength;

            int totalLength = Rlp.LengthOfSequence(contentLength);

            RlpStream rlpStream = new NettyRlpStream(byteBuffer);
            byteBuffer.EnsureWritable(totalLength);

            rlpStream.StartSequence(contentLength);
            rlpStream.Encode(message.RequestId);
            rlpStream.Encode(message.BufferValue);
            ethSerializer.Serialize(byteBuffer, message.EthMessage);
        }

        public BlockBodiesMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyRlpStream rlpStream = new(byteBuffer);
            return Deserialize(rlpStream);
        }

        private static BlockBodiesMessage Deserialize(RlpStream rlpStream)
        {
            BlockBodiesMessage blockBodiesMessage = new();
            rlpStream.ReadSequenceLength();
            blockBodiesMessage.RequestId = rlpStream.DecodeLong();
            blockBodiesMessage.BufferValue = rlpStream.DecodeInt();
            blockBodiesMessage.EthMessage = Eth.V62.Messages.BlockBodiesMessageSerializer.Deserialize(rlpStream);
            return blockBodiesMessage;
        }
    }
}
