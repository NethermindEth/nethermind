// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Les.Messages
{
    public class GetBlockBodiesMessageSerializer : IZeroMessageSerializer<GetBlockBodiesMessage>
    {
        public void Serialize(IByteBuffer byteBuffer, GetBlockBodiesMessage message)
        {
            Eth.V62.Messages.GetBlockBodiesMessageSerializer ethSerializer = new();
            Rlp ethMessage = new(ethSerializer.Serialize(message.EthMessage));
            int contentLength = Rlp.LengthOf(message.RequestId) + ethMessage.Length;

            int totalLength = Rlp.LengthOfSequence(contentLength);

            RlpStream rlpStream = new NettyRlpStream(byteBuffer);
            byteBuffer.EnsureWritable(totalLength);

            rlpStream.StartSequence(contentLength);
            rlpStream.Encode(message.RequestId);
            rlpStream.Encode(ethMessage);
        }

        public GetBlockBodiesMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyRlpStream rlpStream = new(byteBuffer);
            return Deserialize(rlpStream);
        }

        private static GetBlockBodiesMessage Deserialize(RlpStream rlpStream)
        {
            GetBlockBodiesMessage getBlockBodiesMessage = new();
            rlpStream.ReadSequenceLength();
            getBlockBodiesMessage.RequestId = rlpStream.DecodeLong();
            getBlockBodiesMessage.EthMessage = Eth.V62.Messages.GetBlockBodiesMessageSerializer.Deserialize(rlpStream);
            return getBlockBodiesMessage;
        }
    }
}
