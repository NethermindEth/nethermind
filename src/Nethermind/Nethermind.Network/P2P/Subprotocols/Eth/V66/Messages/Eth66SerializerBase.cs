// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages
{
    /// <summary>
    /// Shared serializer for eth request/response messages that prefix their payload with a request id.
    /// </summary>
    public abstract class Eth66SerializerBase<TMessage> : IZeroInnerMessageSerializer<TMessage>
        where TMessage : Eth66MessageBase
    {
        public void Serialize(IByteBuffer byteBuffer, TMessage message)
        {
            int totalLength = GetLength(message, out int contentLength);
            byteBuffer.EnsureWritable(totalLength);

            NettyRlpStream stream = new(byteBuffer);
            stream.StartSequence(contentLength);
            stream.Encode(message.RequestId);
            SerializeInternal(byteBuffer, message);
        }

        public TMessage Deserialize(IByteBuffer byteBuffer) => byteBuffer.DeserializeRlp(Deserialize);

        private TMessage Deserialize(ref Rlp.ValueDecoderContext ctx)
        {
            int sequenceLength = ctx.ReadSequenceLength();
            int checkPosition = ctx.Position + sequenceLength;
            long requestId = ctx.DecodeLong();

            TMessage message = DeserializeInternal(ref ctx, requestId);
            ctx.Check(checkPosition);
            return message;
        }

        public int GetLength(TMessage message, out int contentLength)
        {
            contentLength = Rlp.LengthOf(message.RequestId) + GetLengthInternal(message);
            return Rlp.LengthOfSequence(contentLength);
        }

        protected abstract void SerializeInternal(IByteBuffer byteBuffer, TMessage message);
        protected abstract TMessage DeserializeInternal(ref Rlp.ValueDecoderContext ctx, long requestId);
        protected abstract int GetLengthInternal(TMessage message);
    }
}
