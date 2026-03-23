// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Network;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages
{
    public abstract class Eth66MessageSerializer<TEth66Message> : IZeroInnerMessageSerializer<TEth66Message>
        where TEth66Message : MessageBase, IEth66Message
    {
        public void Serialize(IByteBuffer byteBuffer, TEth66Message message)
        {
            int length = GetLength(message, out int contentLength);
            byteBuffer.EnsureWritable(length);
            RlpStream rlpStream = new NettyRlpStream(byteBuffer);
            rlpStream.StartSequence(contentLength);
            rlpStream.Encode(message.RequestId);
            SerializeInternal(byteBuffer, message);
        }

        public TEth66Message Deserialize(IByteBuffer byteBuffer)
        {
            Rlp.ValueDecoderContext ctx = byteBuffer.AsRlpContext();
            ctx.ReadSequenceLength();
            long requestId = ctx.DecodeLong();
            byteBuffer.SetReaderIndex(byteBuffer.ReaderIndex + ctx.Position);
            return DeserializeInternal(byteBuffer, requestId);
        }

        public int GetLength(TEth66Message message, out int contentLength)
        {
            contentLength =
                Rlp.LengthOf(message.RequestId) +
                GetLengthInternal(message);

            return Rlp.LengthOfSequence(contentLength);
        }

        protected abstract void SerializeInternal(IByteBuffer byteBuffer, TEth66Message message);
        protected abstract TEth66Message DeserializeInternal(IByteBuffer byteBuffer, long requestId);
        protected abstract int GetLengthInternal(TEth66Message message);
    }
}
