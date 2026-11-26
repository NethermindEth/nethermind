// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Serialization.FluentRlp;
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
            RlpWriter writer = RlpWriter.ContentWriter(destination);
            RlpSequence sequence = writer.StartSequence(GetContentLength(message));
            writer.Encode(message.RequestId);
            SerializeInternal(ref writer, message);
            writer.EndSequence(sequence);
            return writer.Length;
        }

        public TMessage Deserialize(ReadOnlySpan<byte> data)
        {
            RlpReader reader = new(data);
            RlpReader ctx = reader.EnterSequence(out int itemLength);
            long requestId = ctx.DecodeLong();

            TMessage message = DeserializeInternal(ref ctx, requestId);
            try
            {
                reader.ExitSequence(in ctx, itemLength);
                reader.CheckEnd();
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
