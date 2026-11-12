// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Network.P2P.Messages;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages
{
    public class Eth66MessageSerializer<TEth66Message, TEthMessage> : IMessageSerializer<TEth66Message>
        where TEth66Message : Eth66Message<TEthMessage>, new()
        where TEthMessage : P2PMessage
    {
        private readonly IMessageSerializer<TEthMessage> _ethMessageSerializer;

        protected Eth66MessageSerializer(IMessageSerializer<TEthMessage> ethMessageSerializer) => _ethMessageSerializer = ethMessageSerializer;

        public int Serialize(Span<byte> destination, TEth66Message message)
        {
            // The wrapped message writes itself, so only the prefix and the request id go through this writer.
            int position = Rlp.StartSequence(destination, 0, Rlp.LengthOf(message.RequestId) + _ethMessageSerializer.GetLength(message.EthMessage));
            RlpWriter writer = RlpWriter.ContentWriter(destination[position..]);
            writer.Encode(message.RequestId);
            position += writer.Length;
            return position + _ethMessageSerializer.Serialize(destination[position..], message.EthMessage);
        }

        public TEth66Message Deserialize(ReadOnlySpan<byte> data)
        {
            long requestId = Eth66MessageEnvelope.ReadRequestId(data, out int ethMessageStart);
            return new TEth66Message
            {
                RequestId = requestId,
                EthMessage = _ethMessageSerializer.Deserialize(data[ethMessageStart..])
            };
        }

        public int GetLength(TEth66Message message) =>
            Rlp.LengthOfSequence(Rlp.LengthOf(message.RequestId) + _ethMessageSerializer.GetLength(message.EthMessage));
    }

    internal static class Eth66MessageEnvelope
    {
        /// <summary>
        /// Reads the request id of a message that is exactly the list in <paramref name="data"/>.
        /// </summary>
        /// <param name="ethMessageStart">The position of the wrapped message, which takes the rest of the list.</param>
        /// <exception cref="RlpReaderException">The list does not take all of <paramref name="data"/>.</exception>
        public static long ReadRequestId(ReadOnlySpan<byte> data, out int ethMessageStart)
        {
            RlpReader reader = new(data);
            RlpReader ctx = reader.EnterSequence(out int itemLength);
            if (itemLength != data.Length)
            {
                ThrowListLengthMismatch();
            }

            long requestId = ctx.DecodeLong();
            ethMessageStart = data.Length - ctx.Remaining;
            return requestId;
        }

        [DoesNotReturn, StackTraceHidden]
        private static void ThrowListLengthMismatch() =>
            throw new RlpReaderException("The eth66 message list does not end where its data does");
    }
}
