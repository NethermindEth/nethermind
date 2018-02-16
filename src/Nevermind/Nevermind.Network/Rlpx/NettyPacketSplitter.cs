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

using System;
using System.Collections.Generic;
using System.Threading;
using DotNetty.Codecs;
using DotNetty.Transport.Channels;
using Nevermind.Core.Encoding;
using Nevermind.Core.Extensions;

namespace Nevermind.Network.Rlpx
{
    public class NettyPacketSplitter : MessageToMessageEncoder<Packet>
    {
        public const int FrameBoundary = 16;

        public const int MaxFrameSize = FrameBoundary * 64;
        private int _contextId;

        protected override void Encode(IChannelHandlerContext context, Packet message, List<object> output)
        {
            Interlocked.Increment(ref _contextId);

            byte[] padded = Pad16(message.Data);
            int framesCount = padded.Length / MaxFrameSize + 1;

            for (int i = 0; i < framesCount; i++)
            {
                // TODO: rlp into existing array
                byte[] packetTypeData = i == 0 ? Rlp.Encode(message.PacketType ?? 0).Bytes : Bytes.Empty; // TODO: check the 0 packet type
                int packetTypeSize = packetTypeData.Length;

                int payloadOffset = MaxFrameSize * i;
                int dataSize = Math.Min(MaxFrameSize, padded.Length - payloadOffset);

                byte[] frame = new byte[16 + 16 + packetTypeData.Length + dataSize + 16]; // header + header MAC + packet type + payload + frame MAC

                frame[0] = (byte)((dataSize + packetTypeSize) >> 16);
                frame[1] = (byte)((dataSize + packetTypeSize) >> 8);
                frame[2] = (byte)(dataSize + packetTypeSize);
                List<object> headerDataItems = new List<object>();
                headerDataItems.Add(message.ProtocolType ?? 0);
                if (framesCount > 1)
                {
                    headerDataItems.Add(_contextId);
                    if (i == 0)
                    {
                        headerDataItems.Add(packetTypeData.Length + padded.Length);
                    }
                }

                // TODO: rlp into existing array
                byte[] headerDataBytes = Rlp.Encode(headerDataItems).Bytes;

                Buffer.BlockCopy(headerDataBytes, 0, frame, 3, headerDataBytes.Length);
                Buffer.BlockCopy(packetTypeData, 0, frame, 32, packetTypeSize);
                Buffer.BlockCopy(padded, payloadOffset, frame, 32 + packetTypeSize, dataSize);

                output.Add(frame);
            }
        }

        private static byte[] Pad16(byte[] data)
        {
            int paddingSize = 16 - data.Length % 16;
            byte[] padded = paddingSize == 16 ? data : Bytes.Concat(data, new byte[paddingSize]);
            return padded;
        }
    }
}