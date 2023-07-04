// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Les.Messages
{
    public class GetReceiptsMessageSerializer : IZeroMessageSerializer<GetReceiptsMessage>
    {
        public void Serialize(IByteBuffer byteBuffer, GetReceiptsMessage message)
        {
            Eth.V63.Messages.GetReceiptsMessageSerializer ethSerializer = new();
            Rlp ethMessage = new(ethSerializer.Serialize(message.EthMessage));
            int contentLength = Rlp.LengthOf(message.RequestId) + ethMessage.Length;

            int totalLength = Rlp.LengthOfSequence(contentLength);

            RlpStream rlpStream = new NettyRlpStream(byteBuffer);
            byteBuffer.EnsureWritable(totalLength);

            rlpStream.StartSequence(contentLength);
            rlpStream.Encode(message.RequestId);
            rlpStream.Encode(ethMessage);
        }

        public GetReceiptsMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyRlpStream rlpStream = new(byteBuffer);
            return Deserialize(rlpStream);
        }

        public static GetReceiptsMessage Deserialize(RlpStream rlpStream)
        {
            GetReceiptsMessage getReceiptsMessage = new();
            rlpStream.ReadSequenceLength();
            getReceiptsMessage.RequestId = rlpStream.DecodeLong();
            getReceiptsMessage.EthMessage = Eth.V63.Messages.GetReceiptsMessageSerializer.Deserialize(rlpStream);
            return getReceiptsMessage;
        }
    }
}
