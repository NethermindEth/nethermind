// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using DotNetty.Buffers;
using Nethermind.Network.P2P.Messages;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages
{
    public class Eth66MessageSerializer<TEth66Message, TEthMessage> : IZeroInnerMessageSerializer<TEth66Message>
        where TEth66Message : Eth66Message<TEthMessage>, new()
        where TEthMessage : P2PMessage
    {
        private readonly IZeroInnerMessageSerializer<TEthMessage> _ethMessageSerializer;

        protected Eth66MessageSerializer(IZeroInnerMessageSerializer<TEthMessage> ethMessageSerializer) => _ethMessageSerializer = ethMessageSerializer;

        public void Serialize(IByteBuffer byteBuffer, TEth66Message message)
        {
            int length = GetLength(message, out int contentLength);
            byteBuffer.EnsureWritable(length);
            // The inner message writes to the buffer itself, so only the prefix and the request id go through this writer.
            int headerLength = length - contentLength + Rlp.LengthOf(message.RequestId);
            RlpWriter writer = byteBuffer.StartRlpWrite(headerLength);
            Span<byte> prefix = stackalloc byte[Rlp.LengthOfSequence(0) + sizeof(int)];
            writer.WriteEncoded(prefix[..Rlp.StartSequence(prefix, 0, contentLength)]);
            writer.Encode(message.RequestId);
            byteBuffer.CompleteRlpWrite(in writer, headerLength);
            _ethMessageSerializer.Serialize(byteBuffer, message.EthMessage);
        }

        public TEth66Message Deserialize(IByteBuffer byteBuffer)
        {
            int startReaderIndex = byteBuffer.ReaderIndex;
            RlpReader reader = new(byteBuffer.AsSpan());
            RlpReader ctx = reader.EnterSequence(out int itemLength);
            TEth66Message eth66Message = new();
            eth66Message.RequestId = ctx.DecodeLong();
            byteBuffer.SetReaderIndex(byteBuffer.ReaderIndex + itemLength - ctx.Remaining);
            eth66Message.EthMessage = _ethMessageSerializer.Deserialize(byteBuffer);

            if (byteBuffer.ReaderIndex - startReaderIndex != itemLength)
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
            throw new RlpReaderException("Unexpected trailing data in eth66 message");
    }
}
