// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using DotNetty.Buffers;
using Nethermind.Network.P2P.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages
{
    public class Eth66MessageSerializer<TEth66Message, TEthMessage> : IZeroInnerMessageSerializer<TEth66Message>
        where TEth66Message : Eth66Message<TEthMessage>, new()
        where TEthMessage : P2PMessage
    {
        private readonly IZeroInnerMessageSerializer<TEthMessage> _ethMessageSerializer;

        protected Eth66MessageSerializer(IZeroInnerMessageSerializer<TEthMessage> ethMessageSerializer) => _ethMessageSerializer = ethMessageSerializer;

        public void Serialize(IByteBuffer byteBuffer, TEth66Message message) => this.SerializeWithLengths(byteBuffer, message);

        public void PopulateLength(TEth66Message message, ref LengthStack lengths)
        {
            _ethMessageSerializer.PopulateLength(message.EthMessage, ref lengths);
            int contentLength = Rlp.LengthOf(message.RequestId) + lengths.Pop();
            lengths.Push(contentLength);
            lengths.Push(Rlp.LengthOfSequence(contentLength));
        }

        public void Serialize(IByteBuffer byteBuffer, TEth66Message message, ref LengthStack lengths)
        {
            ByteBufferRlpWriter writer = new(byteBuffer);
            writer.StartSequence(lengths.Pop());
            writer.Encode(message.RequestId);
            _ethMessageSerializer.Serialize(byteBuffer, message.EthMessage, ref lengths);
        }

        public TEth66Message Deserialize(IByteBuffer byteBuffer)
        {
            int startReaderIndex = byteBuffer.ReaderIndex;
            RlpReader ctx = new(byteBuffer.AsSpan());
            int sequenceLength = ctx.ReadSequenceLength();
            int checkPosition = ctx.Position + sequenceLength;
            TEth66Message eth66Message = new();
            eth66Message.RequestId = ctx.DecodeLong();
            byteBuffer.SetReaderIndex(byteBuffer.ReaderIndex + ctx.Position);
            eth66Message.EthMessage = _ethMessageSerializer.Deserialize(byteBuffer);

            if (byteBuffer.ReaderIndex - startReaderIndex != checkPosition)
            {
                eth66Message.Dispose();
                ThrowUnexpectedTrailingData();
            }

            return eth66Message;
        }

        [DoesNotReturn, StackTraceHidden]
        private static void ThrowUnexpectedTrailingData() =>
            throw new RlpException("Unexpected trailing data in eth66 message");
    }
}
