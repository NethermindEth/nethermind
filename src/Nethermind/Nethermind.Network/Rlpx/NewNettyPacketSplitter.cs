/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.Threading;
using DotNetty.Buffers;
using DotNetty.Codecs;
using DotNetty.Transport.Channels;

namespace Nethermind.Network.Rlpx
{
    public class NewNettyPacketSplitter : MessageToByteEncoder<IByteBuffer>
    {
        private const int FrameBoundary = 16;

        private int _contextId;
        private byte packetType = 1;

        protected override void Encode(IChannelHandlerContext context, IByteBuffer message, IByteBuffer output)
        {
            if (message.ReadableBytes == 1)
            {
                packetType = message.ReadByte();
            }
            else
            {
                Interlocked.Increment(ref _contextId);
                int packetTypeSize = packetType >= 128 ? 2 : 1;
                int totalPayloadSize = packetTypeSize + message.ReadableBytes;
                int paddingSize = totalPayloadSize % FrameBoundary == 0 ? 0 : FrameBoundary - totalPayloadSize % FrameBoundary;

                if (output.WritableBytes < totalPayloadSize + paddingSize + 32 + packetTypeSize)
                {
                    output.DiscardReadBytes();
                }
                
                /*0*/ output.WriteByte((byte) (totalPayloadSize >> 16));
                /*1*/ output.WriteByte((byte) (totalPayloadSize >> 8));
                /*2*/ output.WriteByte((byte) totalPayloadSize);

                /*3*/ output.WriteByte(193);
                /*4*/ output.WriteByte(128);
                
                /*5-32*/ output.WriteZero(27);

                /*1 or 2*/ WritePacketType(output);

                /*message*/ message.ReadBytes(output, message.ReadableBytes);
                /*padding*/
                output.WriteZero(paddingSize);
            }
        }

        private void WritePacketType(IByteBuffer output)
        {
            if (packetType == 0)
            {
                output.WriteByte(128);
            }
            else if (packetType < 128)
            {
                output.WriteByte(packetType);
            }
            else
            {
                output.WriteByte(129);
                output.WriteByte(packetType);
            }
        }
    }
}