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
using System.Threading;
using DotNetty.Buffers;
using DotNetty.Codecs;
using DotNetty.Transport.Channels;
using Nethermind.Core;
using Nethermind.Core.Encoding;
using Nethermind.Logging;

namespace Nethermind.Network.Rlpx
{
    public class ZeroNettyPacketSplitter : MessageToByteEncoder<IByteBuffer>
    {
        public const int FrameBoundary = 16;
        public int MaxFrameSize = FrameBoundary * 64;

        private ILogger _logger;

        public ZeroNettyPacketSplitter(ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public void DisableFraming()
        {
            MaxFrameSize = int.MaxValue;
        }

        private int _contextId;
        private byte packetType = 1;

        [Todo(Improve.Refactor, "We can remove MAC space from here later and move it to encoder")]
        protected override void Encode(IChannelHandlerContext context, IByteBuffer input, IByteBuffer output)
        {
            Interlocked.Increment(ref _contextId);

            packetType = input.ReadByte();

            int packetTypeSize = packetType >= 128 ? 2 : 1;
            int totalPayloadSize = packetTypeSize + input.ReadableBytes;

            int framesCount = (totalPayloadSize - 1) / MaxFrameSize + 1;
            for (int i = 0; i < framesCount; i++)
            {
                int totalPayloadOffset = MaxFrameSize * i;
                int framePayloadSize = Math.Min(MaxFrameSize, totalPayloadSize - totalPayloadOffset);
                int paddingSize = 0;
                if (i == framesCount - 1)
                {
                    // other frames will be Max frame size which is a multiplier of 16
                    paddingSize = totalPayloadSize % 16 == 0 ? 0 : 16 - totalPayloadSize % 16;
                }

                output.MakeSpace(32 + framePayloadSize + paddingSize + 16, "splitter");

                // 000 - 016 | header
                // 016 - 032 | header MAC
                // 032 - 03x | packet type
                // 03x - frm | payload
                // frm - %16 | padding to 16
                // pad - +16 | payload MAC

                /*0*/
                output.WriteByte((byte) (framePayloadSize >> 16));
                /*1*/
                output.WriteByte((byte) (framePayloadSize >> 8));
                /*2*/
                output.WriteByte((byte) framePayloadSize);

                if (framesCount == 1)
                {
                    /*3*/
                    output.WriteByte(193);
                    /*4*/
                    output.WriteByte(128);
                    /*5-32*/
                    output.WriteZero(11);
                }
                else
                {
                    Rlp[] headerDataItems;
                    if (i == 0)
                    {
                        headerDataItems = new Rlp[3];
                        headerDataItems[2] = Rlp.Encode(totalPayloadSize);
                    }
                    else
                    {
                        headerDataItems = new Rlp[2];
                    }

                    headerDataItems[1] = Rlp.Encode(_contextId);
                    headerDataItems[0] = Rlp.Encode(0);
                    byte[] headerDataBytes = Rlp.Encode(headerDataItems).Bytes;
                    output.WriteBytes(headerDataBytes);
                    output.WriteZero(16 - headerDataBytes.Length - 3);
                }

                int framePacketTypeSize = 0;
                if (i == 0)
                {
                    /*33 or 33-34*/
                    framePacketTypeSize = WritePacketType(output);
                }

                /*message*/
                input.ReadBytes(output, framePayloadSize - framePacketTypeSize);
                /*padding to 16*/
                output.WriteZero(paddingSize);
            }
        }

        private int WritePacketType(IByteBuffer output)
        {
            if (packetType == 0)
            {
                output.WriteByte(128);
                return 1;
            }

            if (packetType < 128)
            {
                output.WriteByte(packetType);
                return 1;
            }

            output.WriteByte(129);
            output.WriteByte(packetType);
            return 2;
        }
    }
}