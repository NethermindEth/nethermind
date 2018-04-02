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
using Nethermind.Core.Encoding;

namespace Nethermind.Network.Rlpx
{   
    public class NettyPacketSplitter : MessageToMessageEncoder<Packet>
    {
        public const int FrameBoundary = 16;

        public const int MaxFrameSize = FrameBoundary * 64;
        private int _contextId;

        protected override void Encode(IChannelHandlerContext context, Packet message, List<object> output)
        {
            Interlocked.Increment(ref _contextId);

            byte[] packetTypeData = Rlp.Encode(message.PacketType).Bytes; // TODO: check the 0 packet type
            int packetTypeSize = packetTypeData.Length;
            int totalPayloadSize = packetTypeSize + message.Data.Length;
            
            int framesCount = (totalPayloadSize - 1) / MaxFrameSize + 1;
            for (int i = 0; i < framesCount; i++)
            {
                int totalPayloadOffset = MaxFrameSize * i;
                int framePayloadSize = Math.Min(MaxFrameSize, totalPayloadSize - totalPayloadOffset);
                int paddingSize = 0;
                if (i == framesCount - 1)
                {
                    paddingSize = totalPayloadSize % 16 == 0 ? 0 : 16 - totalPayloadSize % 16;
                }

                byte[] frame = new byte[16 + 16 + framePayloadSize + paddingSize + 16]; // header + header MAC + packet type + payload + frame MAC

                frame[0] = (byte)(framePayloadSize >> 16);
                frame[1] = (byte)(framePayloadSize >> 8);
                frame[2] = (byte)framePayloadSize;
                List<object> headerDataItems = new List<object>();
                
                // seems that with adaptive message IDs we always send protocol ID as 0
//                headerDataItems.Add(message.ProtocolType);
                headerDataItems.Add(0);
                if (framesCount > 1)
                {
                    headerDataItems.Add(_contextId);
                    if (i == 0)
                    {
                        headerDataItems.Add(totalPayloadSize);
                    }
                }

                // TODO: rlp into existing array
                int framePacketTypeSize = i == 0 ? packetTypeData.Length : 0;
                byte[] headerDataBytes = Rlp.Encode(headerDataItems).Bytes;
                Buffer.BlockCopy(headerDataBytes, 0, frame, 3, headerDataBytes.Length);
                Buffer.BlockCopy(packetTypeData, 0, frame, 32, framePacketTypeSize);
                Buffer.BlockCopy(message.Data, totalPayloadOffset - packetTypeSize + framePacketTypeSize, frame, 32 + framePacketTypeSize, framePayloadSize - framePacketTypeSize);

                output.Add(frame);
            }
        }
    }
}