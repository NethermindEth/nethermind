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
        public void Serialize(IByteBuffer byteBuffer, TMessage message) => this.SerializeWithLengths(byteBuffer, message);

        public void PopulateLength(TMessage message, ref LengthStack lengths)
        {
            PopulateLengthInternal(message, ref lengths);
            int contentLength = Rlp.LengthOf(message.RequestId) + lengths.Peek();
            lengths.Push(contentLength);
            lengths.Push(Rlp.LengthOfSequence(contentLength));
        }

        public void Serialize(IByteBuffer byteBuffer, TMessage message, ref LengthStack lengths)
        {
            lengths.Pop();
            ByteBufferRlpWriter writer = new(byteBuffer);
            writer.StartSequence(lengths.Pop());
            writer.Encode(message.RequestId);
            SerializeInternal(byteBuffer, message, ref lengths);
        }

        public virtual TMessage Deserialize(IByteBuffer byteBuffer) => byteBuffer.DeserializeRlp(Deserialize);

        private TMessage Deserialize(ref RlpReader ctx)
        {
            int sequenceLength = ctx.ReadSequenceLength();
            int checkPosition = ctx.Position + sequenceLength;
            long requestId = ctx.DecodeLong();

            TMessage message = DeserializeInternal(ref ctx, requestId);
            try
            {
                ctx.Check(checkPosition);
                return message;
            }
            catch
            {
                message.Dispose();
                throw;
            }
        }

        /// <summary>Pushes the lengths <see cref="SerializeInternal"/> needs, then the length of the payload after the request id.</summary>
        protected abstract void PopulateLengthInternal(TMessage message, ref LengthStack lengths);

        /// <summary>Pops the payload length <see cref="PopulateLengthInternal"/> pushed last, then the rest, and writes the payload.</summary>
        protected abstract void SerializeInternal(IByteBuffer byteBuffer, TMessage message, ref LengthStack lengths);
        protected abstract TMessage DeserializeInternal(ref RlpReader ctx, long requestId);
    }
}
