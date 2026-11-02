// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Network.P2P.Messages;
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
            RlpWriter writer = new(destination);
            writer.StartSequence(Rlp.LengthOf(message.RequestId) + _ethMessageSerializer.GetLength(message.EthMessage));
            writer.Encode(message.RequestId);
            return writer.Position + _ethMessageSerializer.Serialize(destination[writer.Position..], message.EthMessage);
        }

        public TEth66Message Deserialize(ReadOnlySpan<byte> data)
        {
            long requestId = ReadRequestId(data, out int ethMessageStart);
            return new TEth66Message
            {
                RequestId = requestId,
                EthMessage = _ethMessageSerializer.Deserialize(data[ethMessageStart..])
            };
        }

        public int GetLength(TEth66Message message) =>
            Rlp.LengthOfSequence(Rlp.LengthOf(message.RequestId) + _ethMessageSerializer.GetLength(message.EthMessage));

        /// <summary>
        /// Reads the request id of a message that is exactly the list in <paramref name="data"/>.
        /// </summary>
        /// <param name="ethMessageStart">The position of the wrapped message, which takes the rest of the list.</param>
        /// <exception cref="RlpException">The list does not take all of <paramref name="data"/>.</exception>
        protected static long ReadRequestId(ReadOnlySpan<byte> data, out int ethMessageStart)
        {
            RlpReader ctx = new(data);
            int sequenceLength = ctx.ReadSequenceLength();
            if (ctx.Position + sequenceLength != data.Length)
            {
                ThrowListLengthMismatch();
            }

            long requestId = ctx.DecodeLong();
            ethMessageStart = ctx.Position;
            return requestId;
        }

        [DoesNotReturn, StackTraceHidden]
        private static void ThrowListLengthMismatch() =>
            throw new RlpException("The eth66 message list does not end where its data does");
    }
}
