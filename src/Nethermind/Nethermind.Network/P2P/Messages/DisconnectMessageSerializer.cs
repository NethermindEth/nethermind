//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
//
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Linq;
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
            if (msg.SequenceEqual(breach1))
            {
                return new DisconnectMessage(DisconnectReason.Breach1);
            }

            if (msg.SequenceEqual(breach2))
            {
                return new DisconnectMessage(DisconnectReason.Breach2);
            }

            Rlp.ValueDecoderContext rlpStream = msg.AsRlpValueContext();
            rlpStream.ReadSequenceLength();
            int reason = rlpStream.DecodeInt();
            DisconnectMessage disconnectMessage = new(reason);
            return disconnectMessage;
        }
    }
}
