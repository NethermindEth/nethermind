// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using DotNetty.Buffers;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P.Messages
{
    public class DisconnectMessageSerializer : IZeroMessageSerializer<DisconnectMessage>
    {
        public void Serialize(IByteBuffer byteBuffer, DisconnectMessage msg)
        {
            int length = GetLength(msg, out int contentLength);
            byteBuffer.EnsureWritable(length);
            NettyRlpStream rlpStream = new(byteBuffer);

            rlpStream.StartSequence(contentLength);
            rlpStream.Encode((byte)msg.Reason);
        }

        private int GetLength(DisconnectMessage message, out int contentLength)
        {
            contentLength = Rlp.LengthOf((byte)message.Reason);

            return Rlp.LengthOfSequence(contentLength);
        }


        public DisconnectMessage Deserialize(IByteBuffer msgBytes)
        {
            if (msgBytes.ReadableBytes == 1)
            {
                return new DisconnectMessage((DisconnectReason)msgBytes.GetByte(0));
            }

            if (msgBytes.ReadableBytes == 0)
            {
                // Sometimes 0x00 was sent, uncompressed, which interpreted as empty buffer by snappy.
                return new DisconnectMessage(DisconnectReason.DisconnectRequested);
            }

            Span<byte> msg = msgBytes.ReadAllBytesAsSpan();
            Rlp.ValueDecoderContext rlpStream = msg.AsRlpValueContext();
            if (!rlpStream.IsSequenceNext())
            {
                rlpStream = new Rlp.ValueDecoderContext(rlpStream.DecodeByteArraySpan());
            }

            rlpStream.ReadSequenceLength();
            int reason = rlpStream.DecodeInt();
            DisconnectMessage disconnectMessage = new DisconnectMessage(reason);
            return disconnectMessage;
        }
    }
}
