// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
                output.WriteByte((byte)(framePayloadSize >> 16));
                /*1*/
                output.WriteByte((byte)(framePayloadSize >> 8));
                /*2*/
                output.WriteByte((byte)framePayloadSize);

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
                    NettyRlpStream stream = new(output);
                    int contentLength = Rlp.LengthOf(_contextId) + Rlp.LengthOf(0);
                    if (i == 0)
                    {
                        contentLength += Rlp.LengthOf(totalPayloadSize);
                    }
                    output.EnsureWritable(Rlp.LengthOfSequence(contentLength));
                    stream.StartSequence(contentLength);
                    stream.Encode(0);
                    stream.Encode(_contextId);
                    if (i == 0)
                    {
                        stream.Encode(totalPayloadSize);
                    }
                    output.WriteZero(Frame.HeaderSize - Rlp.LengthOfSequence(contentLength) - 3);
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
