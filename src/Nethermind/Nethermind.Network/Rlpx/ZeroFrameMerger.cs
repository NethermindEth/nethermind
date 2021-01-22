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
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using DotNetty.Buffers;
using DotNetty.Codecs;
using DotNetty.Common.Utilities;
using DotNetty.Transport.Channels;
using Nethermind.Logging;

namespace Nethermind.Network.Rlpx
{
    public class ZeroFrameMerger : ByteToMessageDecoder
    {
        private ILogger _logger;

        private ZeroPacket _zeroPacket;
        private FrameHeaderReader _headerReader = new FrameHeaderReader();

        public ZeroFrameMerger(ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger<ZeroFrameMerger>() ?? throw new ArgumentNullException(nameof(logManager));
        }

        protected override void Decode(IChannelHandlerContext context, IByteBuffer input, List<object> output)
        {
            // Note that each input is a full frame header16|payload that are automatically released by the base class.
            // If the input is not a full and valid frame we can throw as this is an unexpected behaviour from the
            // decoder up the pipeline.

            // Moreover we will never receive more than a full packet in a single input so the input buffer
            // is expected to have no readable bytes after the merging operation.

            if (_logger.IsTrace) _logger.Trace("Merging frames");
            if (input.ReferenceCount != 1)
            {
                throw new IllegalReferenceCountException(input.ReferenceCount);
            }

            FrameHeaderReader.FrameInfo frame = _headerReader.ReadFrameHeader(input);
            if (frame.IsFirst)
            {
                ReadFirstChunk(context, input, frame);
            }
            else
            {
                ReadChunk(input, frame);
            }

            if (!_zeroPacket.Content.IsWritable())
            {
                input.SkipBytes(frame.Padding);
                output.Add(_zeroPacket);
                _zeroPacket = null;

                if (input.IsReadable())
                {
                    throw new CorruptedFrameException($"{nameof(ZeroFrameMerger)} received a corrupted frame - {input.ReadableBytes} longer than expected");
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ReadChunk(IByteBuffer input, FrameHeaderReader.FrameInfo frame)
        {
            input.ReadBytes(_zeroPacket.Content, frame.Size);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ReadFirstChunk(IChannelHandlerContext context, IByteBuffer input, FrameHeaderReader.FrameInfo frame)
        {
            byte packetTypeRlp = input.ReadByte();
            IByteBuffer content;
            if (frame.IsChunked)
            {
                content = context.Allocator.Buffer(frame.TotalPacketSize - 1);
            }
            else
            {
                content = input.ReadSlice(frame.Size - 1);

                // Since we will call release in the next handler and we use a derived buffer here
                // we need to call Retain to prevent the buffer from being released twice
                // (once in the next handler and once in the base class).
                content.Retain();
            }

            _zeroPacket = new ZeroPacket(content);
            _zeroPacket.PacketType = GetPacketType(packetTypeRlp);

            // If not chunked then we already used a slice of the input,
            // otherwise we need to read into the freshly allocated buffer.
            if (frame.IsChunked)
            {
                input.ReadBytes(_zeroPacket.Content, frame.Size - 1);
                // do not call Release since the input buffer is managed by 
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte GetPacketType(byte packetTypeRlp)
        {
            return packetTypeRlp == 128 ? (byte) 0 : packetTypeRlp;
        }
    }
}
