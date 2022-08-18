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
using System.Threading;
using DotNetty.Buffers;
using DotNetty.Codecs;
using DotNetty.Transport.Channels;
using Nethermind.Core.Attributes;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Rlpx
{
    public class ZeroPacketSplitter : MessageToByteEncoder<IByteBuffer>, IFramingAware
    {
        private ILogger _logger;

        public ZeroPacketSplitter(ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger<ZeroPacketSplitter>() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public void DisableFraming()
        {
            MaxFrameSize = int.MaxValue;
        }
        
        public int MaxFrameSize { get; private set; } = Frame.DefaultMaxFrameSize;

        private int _contextId;

        [Todo(Improve.Refactor, "We can remove MAC space from here later and move it to encoder")]
        protected override void Encode(IChannelHandlerContext context, IByteBuffer input, IByteBuffer output)
        {
            Interlocked.Increment(ref _contextId);

            int packetType = input.ReadByte();

            int packetTypeSize = packetType >= 128 ? 2 : 1;
            int totalPayloadSize = packetTypeSize + input.ReadableBytes;

            int framesCount = (totalPayloadSize - 1) / MaxFrameSize + 1;
            for (int i = 0; i < framesCount; i++)
            {
                int totalPayloadOffset = MaxFrameSize * i;
                int framePayloadSize = Math.Min(MaxFrameSize, totalPayloadSize - totalPayloadOffset);
                int paddingSize = i == framesCount - 1 ? Frame.CalculatePadding(totalPayloadSize) : 0;
                output.EnsureWritable(Frame.HeaderSize + framePayloadSize + paddingSize, true);

                // 000 - 016 | header
                // 016 - 01x | packet type
                // 01x - frm | payload
                // frm - %16 | padding to 16

                // here we encode payload size as an RLP encoded long value without leading zeros
                /*0*/
                output.WriteByte((byte) (framePayloadSize >> 16));
                /*1*/
                output.WriteByte((byte) (framePayloadSize >> 8));
                /*2*/
                output.WriteByte((byte) framePayloadSize);

                if (framesCount == 1)
                {
                    // // commented out after Trinity reported #2052
                    // // not 100% sure they are right but they may be 
                    // // 193|128 is an RLP encoded array with one element that is zero
                    // /*3*/
                    // output.WriteByte(193);
                    // /*4*/
                    // output.WriteByte(128);
                    // /*5-16*/
                    // output.WriteZero(11);
                    
                    // 194|128 is an RLP encoded array with two elements that are zero
                    /*3*/
                    output.WriteByte(194);
                    /*4*/
                    output.WriteByte(128);
                    /*5*/
                    output.WriteByte(128);
                    /*6-16*/
                    output.WriteZero(10);
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
                    output.WriteZero(Frame.HeaderSize - headerDataBytes.Length - 3);
                }

                int framePacketTypeSize = 0;
                if (i == 0)
                {
                    /*33 or 33-34*/
                    framePacketTypeSize = WritePacketType(packetType, output);
                }

                /*message*/
                input.ReadBytes(output, framePayloadSize - framePacketTypeSize);
                /*padding to 16*/
                output.WriteZero(paddingSize);
            }
        }

        private int WritePacketType(int packetType, IByteBuffer output)
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
