// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages
{
    /// <summary>
    /// Shared serializer for eth request/response messages that prefix their payload with a request id.
    /// </summary>
    public abstract class Eth66SerializerBase<TMessage> : IMessageSerializer<TMessage>
        where TMessage : Eth66MessageBase
    {
        public int Serialize(Span<byte> destination, TMessage message)
        {
            RlpWriter writer = new(destination);
            writer.StartSequence(GetContentLength(message));
            writer.Encode(message.RequestId);
            SerializeInternal(ref writer, message);
            return writer.Position;
        }

        public TMessage Deserialize(ReadOnlySpan<byte> data)
        {
            RlpReader ctx = new(data);
            int sequenceLength = ctx.ReadSequenceLength();
            int checkPosition = ctx.Position + sequenceLength;
            long requestId = ctx.DecodeLong();

            TMessage message = DeserializeInternal(ref ctx, requestId);
            try
            {
                ctx.Check(checkPosition);
                ctx.CheckEnd();
                return message;
            }
            catch
            {
                message.Dispose();
                throw;
            }
        }

        public int GetLength(TMessage message) => Rlp.LengthOfSequence(GetContentLength(message));

        private int GetContentLength(TMessage message) => Rlp.LengthOf(message.RequestId) + GetLengthInternal(message);

        protected abstract void SerializeInternal(ref RlpWriter writer, TMessage message);
        protected abstract TMessage DeserializeInternal(ref RlpReader ctx, long requestId);
        protected abstract int GetLengthInternal(TMessage message);
    }
}
