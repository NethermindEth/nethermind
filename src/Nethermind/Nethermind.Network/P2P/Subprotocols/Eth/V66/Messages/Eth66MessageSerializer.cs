// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Network.P2P.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages
{
    public class Eth66MessageSerializer<TEth66Message, TEthMessage> : IZeroMessageSerializer<TEth66Message>
        where TEth66Message : Eth66Message<TEthMessage>, new()
        where TEthMessage : P2PMessage
    {
        private readonly IZeroMessageSerializer<TEthMessage> _ethMessageSerializer;

        protected Eth66MessageSerializer(IZeroMessageSerializer<TEthMessage> ethMessageSerializer) => _ethMessageSerializer = ethMessageSerializer;

        public void Serialize(Span<byte> buffer, TEth66Message message)
        {
            GetLength(message, out int contentLength);
            RlpWriter writer = new(buffer);
            writer.StartSequence(contentLength);
            writer.Encode(message.RequestId);
            _ethMessageSerializer.Serialize(buffer.Slice(writer.Position), message.EthMessage);
        }

        public TEth66Message Deserialize(ReadOnlySpan<byte> data, out int consumed)
        {
            RlpReader ctx = new(data);
            int sequenceLength = ctx.ReadSequenceLength();
            int checkPosition = ctx.Position + sequenceLength;
            TEth66Message eth66Message = new();
            eth66Message.RequestId = ctx.DecodeLong();
            eth66Message.EthMessage = _ethMessageSerializer.Deserialize(data.Slice(ctx.Position), out int innerConsumed);
            consumed = ctx.Position + innerConsumed;

            if (consumed != checkPosition)
            {
                eth66Message.Dispose();
                ThrowUnexpectedTrailingData();
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

        [DoesNotReturn, StackTraceHidden]
        private static void ThrowUnexpectedTrailingData() =>
            throw new RlpException("Unexpected trailing data in eth66 message");
    }
}
