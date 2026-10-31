// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using DotNetty.Buffers;
using Nethermind.Serialization.FluentRlp;
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

            // The inner message writes to the buffer itself, so only the prefix and the request id go through this writer.
            int headerLength = totalLength - contentLength + Rlp.LengthOf(message.RequestId);
            RlpWriter writer = byteBuffer.StartRlpWrite(headerLength);
            Span<byte> prefix = stackalloc byte[Rlp.LengthOfSequence(0) + sizeof(int)];
            writer.WriteEncoded(prefix[..Rlp.StartSequence(prefix, 0, contentLength)]);
            writer.Encode(message.RequestId);
            byteBuffer.CompleteRlpWrite(in writer, headerLength);
            SerializeInternal(byteBuffer, message);
        }

        public virtual TMessage Deserialize(IByteBuffer byteBuffer) => byteBuffer.DeserializeRlp(Deserialize);

        private TMessage Deserialize(ref RlpReader reader)
        {
            RlpReader ctx = reader.EnterSequence(out int itemLength);
            long requestId = ctx.DecodeLong();

            TMessage message = DeserializeInternal(ref ctx, requestId);
            try
            {
                reader.ExitSequence(ctx, itemLength);
                return message;
            }
            catch
            {
                message.Dispose();
                throw;
            }
        }

        public int GetLength(TMessage message, out int contentLength)
        {
            contentLength = Rlp.LengthOf(message.RequestId) + GetLengthInternal(message);
            return Rlp.LengthOfSequence(contentLength);
        }

        protected abstract void SerializeInternal(IByteBuffer byteBuffer, TMessage message);
        protected abstract TMessage DeserializeInternal(ref RlpReader ctx, long requestId);
        protected abstract int GetLengthInternal(TMessage message);
    }
}
