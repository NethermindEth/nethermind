// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using DotNetty.Buffers;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P.Messages
{
    public class DisconnectMessageSerializer : IZeroMessageSerializer<DisconnectMessage>
    {
        public void Serialize(IByteBuffer byteBuffer, DisconnectMessage msg)
        {
            int length = GetLength(msg, out int contentLength);
            byteBuffer.EnsureWritable(length, force: true);
            ByteBufferRlpWriter writer = new(byteBuffer);

            writer.StartSequence(contentLength);
            writer.Encode((byte)msg.Reason);
        }

        private static int GetLength(DisconnectMessage message, out int contentLength)
        {
            contentLength = Rlp.LengthOf((byte)message.Reason);

            return Rlp.LengthOfSequence(contentLength);
        }


        public DisconnectMessage Deserialize(IByteBuffer msgBytes)
        {
            if (msgBytes.ReadableBytes == 0)
            {
                // Sometimes 0x00 was sent, uncompressed, which interpreted as empty buffer by snappy.
                return new DisconnectMessage(EthDisconnectReason.DisconnectRequested);
            }

            Span<byte> msg = msgBytes.ReadAllBytesAsSpan();
            if (msg.Length == 1 && msg[0] < Rlp.EmptyByteArrayByte)
            {
                return new DisconnectMessage(msg[0]);
            }

            RlpReader reader = new(msg);
            CheckItem(reader);
            if (!reader.IsSequenceNext())
            {
                ReadOnlySpan<byte> wrapped = reader.DecodeByteArraySpan();
                if (wrapped.IsEmpty) return new DisconnectMessage(EthDisconnectReason.DisconnectRequested);
                reader = new RlpReader(wrapped);
                CheckItem(reader);
            }

            int contentLength = reader.ReadSequenceLength();
            if (contentLength == 0) return new DisconnectMessage(EthDisconnectReason.DisconnectRequested);
            reader = new RlpReader(reader.Read(contentLength));
            CheckItem(reader);
            int reason = reader.DecodeInt();
            DisconnectMessage disconnectMessage = new(reason);
            return disconnectMessage;

            static void CheckItem(RlpReader item)
            {
                if (item.Length == 0 || item.PeekNextRlpLength() > item.Length)
                    throw new RlpException("Truncated disconnect payload.");
            }
        }
    }
}
