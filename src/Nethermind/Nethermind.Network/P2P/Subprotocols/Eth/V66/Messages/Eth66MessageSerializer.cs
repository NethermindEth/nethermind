// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Network.P2P.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages
{
    public class Eth66MessageSerializer<TEth66Message, TEthMessage> : IZeroInnerMessageSerializer<TEth66Message>
        where TEth66Message : Eth66Message<TEthMessage>, new()
        where TEthMessage : P2PMessage
    {
        private readonly IZeroInnerMessageSerializer<TEthMessage> _ethMessageSerializer;

        protected Eth66MessageSerializer(IZeroInnerMessageSerializer<TEthMessage> ethMessageSerializer)
        {
            _ethMessageSerializer = ethMessageSerializer;
        }

        public void Serialize(IByteBuffer byteBuffer, TEth66Message message)
        {
            int length = GetLength(message, out int contentLength);
            byteBuffer.EnsureWritable(length);
            RlpStream rlpStream = new NettyRlpStream(byteBuffer);
            rlpStream.StartSequence(contentLength);
            rlpStream.Encode(message.RequestId);
            _ethMessageSerializer.Serialize(byteBuffer, message.EthMessage);
        }

        public TEth66Message Deserialize(IByteBuffer byteBuffer)
        {
            int startReaderIndex = byteBuffer.ReaderIndex;
            Rlp.ValueDecoderContext ctx = byteBuffer.AsRlpContext();
            int sequenceLength = ctx.ReadSequenceLength();
            int checkPosition = ctx.Position + sequenceLength;
            TEth66Message eth66Message = new();
            eth66Message.RequestId = ctx.DecodeLong();
            byteBuffer.SetReaderIndex(byteBuffer.ReaderIndex + ctx.Position);
            eth66Message.EthMessage = _ethMessageSerializer.Deserialize(byteBuffer);

            if (byteBuffer.ReaderIndex - startReaderIndex != checkPosition)
            {
                throw new RlpException("Unexpected trailing data in eth66 message");
            }

            return eth66Message;
        }

        public int GetLength(TEth66Message message, out int contentLength)
        {
            int innerMessageLength = _ethMessageSerializer.GetLength(message.EthMessage, out _);
            contentLength =
                Rlp.LengthOf(message.RequestId) +
                innerMessageLength;

            return Rlp.LengthOfSequence(contentLength);
        }
    }
}
