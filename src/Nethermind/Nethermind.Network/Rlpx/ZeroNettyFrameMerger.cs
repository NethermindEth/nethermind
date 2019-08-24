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
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using DotNetty.Buffers;
using DotNetty.Codecs;
using DotNetty.Common.Utilities;
using DotNetty.Transport.Channels;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Network.Rlpx
{
    public class NettyPacket : DefaultByteBufferHolder
    {
        public byte PacketType { get; set; }

        public NettyPacket(IByteBuffer data) : base(data)
        {
        }
    }

    public class ZeroNettyFrameMerger : ByteToMessageDecoder
    {
        private ILogger _logger;
        private int? _currentContextId;

        private NettyPacket _nettyPacket;
        private byte[] _headerBytes = new byte[FrameParams.HeaderSize];

        public ZeroNettyFrameMerger(ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        protected override void Decode(IChannelHandlerContext context, IByteBuffer input, List<object> output)
        {
            // Note that each input is a full frame header16|payload that we should release after reading.
            // If the input is not a full and valid frame we can throw as this is an unexpected behaviour from the
            // decoder up the pipeline.

            // Moreover we will never receive more than a full packet in a single input so the input buffer
            // is expected to have no readable bytes after the merging operation.

            if (_logger.IsTrace) _logger.Trace("Merging frames");
            if (input.ReferenceCount != 1)
            {
                throw new IllegalReferenceCountException(input.ReferenceCount);
            }

            FrameInfo frame = ReadFrameHeader(input);
            if (frame.IsFirst)
            {
                ReadFirstChunk(context, input, frame);
            }
            else
            {
                ReadChunk(input, frame);
            }

            if (!_nettyPacket.Content.IsWritable())
            {
                input.SkipBytes(frame.Padding);
                output.Add(_nettyPacket);
                _nettyPacket = null;

                if (input.IsReadable())
                {
                    throw new CorruptedFrameException($"{nameof(ZeroNettyFrameMerger)} received a corrupted frame - {input.ReadableBytes} longer than expected");
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ReadChunk(IByteBuffer input, FrameInfo frame)
        {
            input.ReadBytes(_nettyPacket.Content, frame.Size);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ReadFirstChunk(IChannelHandlerContext context, IByteBuffer input, FrameInfo frame)
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

            _nettyPacket = new NettyPacket(content);
            _nettyPacket.PacketType = GetPacketType(packetTypeRlp);

            // If not chunked then we already used a slice of the input,
            // otherwise we need to read into the freshly allocated buffer.
            if (frame.IsChunked)
            {
                input.ReadBytes(_nettyPacket.Content, frame.Size - 1);
                // do not call Release since the input buffer is managed by 
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte GetPacketType(byte packetTypeRlp)
        {
            return packetTypeRlp == 128 ? (byte) 0 : packetTypeRlp;
        }

        private FrameInfo ReadFrameHeader(IByteBuffer input)
        {
            input.ReadBytes(_headerBytes);
            int frameSize = _headerBytes[0] & 0xFF;
            frameSize = (frameSize << 8) + (_headerBytes[1] & 0xFF);
            frameSize = (frameSize << 8) + (_headerBytes[2] & 0xFF);

            Rlp.ValueDecoderContext headerBodyItems = _headerBytes.AsSpan().Slice(3, 13).AsRlpValueContext();
            int headerDataEnd = headerBodyItems.ReadSequenceLength() + headerBodyItems.Position;
            int numberOfItems = headerBodyItems.ReadNumberOfItemsRemaining(headerDataEnd);
            headerBodyItems.DecodeInt(); // not needed - adaptive IDs - DO NOT COMMENT OUT!!! - decode takes int of the RLP sequence and moves the position
            int? contextId = numberOfItems > 1 ? headerBodyItems.DecodeInt() : (int?) null;
            _currentContextId = contextId;
            int? totalPacketSize = numberOfItems > 2 ? headerBodyItems.DecodeInt() : (int?) null;

            bool isChunked = totalPacketSize.HasValue || contextId.HasValue && _currentContextId == contextId && contextId != 0;
            bool isFirst = totalPacketSize.HasValue || !isChunked;
            return new FrameInfo(isChunked, isFirst, frameSize, totalPacketSize ?? frameSize);
        }

        public override void ExceptionCaught(IChannelHandlerContext context, Exception exception)
        {
            _logger.Warn(exception.ToString());

            //In case of SocketException we log it as debug to avoid noise
            if (exception is SocketException)
            {
                if (_logger.IsTrace)
                {
                    _logger.Trace($"Error when merging frames (SocketException): {exception}");
                }
            }
            else
            {
                if (_logger.IsDebug)
                {
                    _logger.Debug($"Error when merging frames: {exception}");
                }
            }

            base.ExceptionCaught(context, exception);
        }

        private struct FrameInfo
        {
            public FrameInfo(bool isChunked, bool isFirst, int size, int totalPacketSize)
            {
                IsChunked = isChunked;
                IsFirst = isFirst;
                Size = size;
                TotalPacketSize = totalPacketSize;
            }

            public bool IsChunked { get; }
            public bool IsFirst { get; }
            public int Size { get; }
            public int TotalPacketSize { get; }
            public int Padding => FrameParams.CalculatePadding(Size);
        }
    }
}