// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
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


        private byte[] breach1 = Bytes.FromHexString("0204c104");
        private byte[] breach2 = Bytes.FromHexString("0204c180");

        public DisconnectMessage Deserialize(IByteBuffer msgBytes)
        {
            if (msgBytes.ReadableBytes == 1)
            {
                return new DisconnectMessage((DisconnectReason)msgBytes.GetByte(0));
            }

            Span<byte> msg = msgBytes.ReadAllBytesAsSpan();
            if (msg.SequenceEqual(breach1)
                || msg.SequenceEqual(breach2))
            {
                return new DisconnectMessage(DisconnectReason.Other);
            }

            Rlp.ValueDecoderContext rlpStream = msg.AsRlpValueContext();
            rlpStream.ReadSequenceLength();
            int reason = rlpStream.DecodeInt();
            DisconnectMessage disconnectMessage = new DisconnectMessage(reason);
            return disconnectMessage;
        }
    }
}
