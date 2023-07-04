// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Les.Messages
{
    public class GetBlockHeadersMessageSerializer : IZeroMessageSerializer<GetBlockHeadersMessage>
    {
        public void Serialize(IByteBuffer byteBuffer, GetBlockHeadersMessage message)
        {
            Eth.V62.Messages.GetBlockHeadersMessageSerializer ethSerializer = new();
            Rlp ethMessage = new(ethSerializer.Serialize(message.EthMessage));
            int contentLength = Rlp.LengthOf(message.RequestId) + ethMessage.Length;

            int totalLength = Rlp.LengthOfSequence(contentLength);

            RlpStream rlpStream = new NettyRlpStream(byteBuffer);
            byteBuffer.EnsureWritable(totalLength, true);

            rlpStream.StartSequence(contentLength);
            rlpStream.Encode(message.RequestId);
            rlpStream.Encode(ethMessage);
        }

        public GetBlockHeadersMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyRlpStream rlpStream = new(byteBuffer);
            return Deserialize(rlpStream);
        }

        private static GetBlockHeadersMessage Deserialize(RlpStream rlpStream)
        {
            GetBlockHeadersMessage getBlockHeadersMessage = new();
            rlpStream.ReadSequenceLength();
            getBlockHeadersMessage.RequestId = rlpStream.DecodeLong();
            getBlockHeadersMessage.EthMessage = Eth.V62.Messages.GetBlockHeadersMessageSerializer.Deserialize(rlpStream);
            return getBlockHeadersMessage;
        }
    }
}
