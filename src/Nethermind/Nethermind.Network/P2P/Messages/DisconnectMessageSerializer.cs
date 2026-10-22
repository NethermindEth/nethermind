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
            if (msgBytes.ReadableBytes == 1)
            {
                return new DisconnectMessage((EthDisconnectReason)msgBytes.GetByte(0));
            }

            if (msgBytes.ReadableBytes == 0)
            {
                // Sometimes 0x00 was sent, uncompressed, which interpreted as empty buffer by snappy.
                return new DisconnectMessage(EthDisconnectReason.DisconnectRequested);
            }

            Span<byte> msg = msgBytes.ReadAllBytesAsSpan();
            RlpReader reader = new(msg);
            if (!reader.IsSequenceNext())
            {
                reader = new RlpReader(reader.DecodeByteArraySpan());
            }

            reader.ReadSequenceLength();
            int reason = reader.DecodeInt();
            DisconnectMessage disconnectMessage = new(reason);
            return disconnectMessage;
        }
    }
}
