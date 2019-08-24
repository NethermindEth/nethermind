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
using DotNetty.Buffers;
using DotNetty.Codecs;
using DotNetty.Common.Utilities;
using DotNetty.Transport.Channels;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Network.Rlpx
{
    public class ZeroNettyFrameMerger : ByteToMessageDecoder
    {
        public const int MaxChunkedFrameSize = FrameBoundary * 64;
        private const int FrameBoundary = 16;
        private const int HeaderSize = 16;
        private const int MacSize = 16;
        private int _remaining;
        private int? _currentContextId;
        private ILogger _logger;

        private IByteBuffer _innerBuffer; // spec used to say that the chunks can come mixed but probably not needed any more

        public ZeroNettyFrameMerger(ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        protected override void Decode(IChannelHandlerContext context, IByteBuffer input, List<object> output)
        {
            try
            {
                if (input.ReferenceCount != 1)
                {
                    throw new IllegalReferenceCountException(input.ReferenceCount);
                }
                
                while (input.IsReadable())
                {
                    if (_logger.IsTrace)
                    {
                        _logger.Trace("Merging frames");
                    }

                    byte[] header = new byte[HeaderSize];
                    input.ReadBytes(header);

                    Rlp.ValueDecoderContext headerBodyItems = header.Slice(3, 13).AsRlpValueContext();
                    int headerDataEnd = headerBodyItems.ReadSequenceLength() + headerBodyItems.Position;
                    int numberOfItems = headerBodyItems.ReadNumberOfItemsRemaining(headerDataEnd);
                    headerBodyItems.DecodeInt(); // not needed - adaptive IDs - DO NOT COMMENT OUT!!! - decode takes int of the RLP sequence and moves the position
                    int? contextId = numberOfItems > 1 ? headerBodyItems.DecodeInt() : (int?) null;
                    int? totalPacketSize = numberOfItems > 2 ? headerBodyItems.DecodeInt() : (int?) null;

                    bool isChunked = totalPacketSize.HasValue || contextId.HasValue && _currentContextId == contextId && contextId != 0;
                    _currentContextId = contextId;
                    if (isChunked)
                    {
                        Debug.Assert(contextId.HasValue);
                        if (_logger.IsTrace)
                        {
                            _logger.Trace("Merging chunked packet");
                        }

                        bool isFirstChunk = totalPacketSize.HasValue;
                        if (isFirstChunk)
                        {
                            _remaining = totalPacketSize.Value - 1; // packet type data size
                            _innerBuffer = PooledByteBufferAllocator.Default.Buffer(totalPacketSize.Value);
                            _innerBuffer.WriteByte(input.ReadByte());
                        }

                        _logger.Error($"{totalPacketSize}");
                        Debug.Assert(_innerBuffer != null, $"TPS {totalPacketSize} CID {contextId} CCID {_currentContextId}");
                        int frameSize = Math.Min(_remaining, MaxChunkedFrameSize) - (isFirstChunk ? 1 : 0);

                        input.ReadBytes(_innerBuffer, frameSize);
                        _remaining -= frameSize;
                        if (_remaining == 0)
                        {
                            input.SkipBytes(FrameParams.CalculatePadding(frameSize));
                            output.Add(_innerBuffer);
                            _innerBuffer = null;
                        }
                    }
                    else
                    {
                        int totalBodySize = header[0] & 0xFF;
                        totalBodySize = (totalBodySize << 8) + (header[1] & 0xFF);
                        totalBodySize = (totalBodySize << 8) + (header[2] & 0xFF);

                        byte packetTypeRlp = input.ReadByte();

                        if (_logger.IsTrace)
                        {
                            _logger.Trace($"Merging single frame packet of length {totalBodySize - 1}");
                        }

                        IByteBuffer outputBuffer = PooledByteBufferAllocator.Default.Buffer(totalBodySize);
                        outputBuffer.WriteByte(GetPacketType(packetTypeRlp));
                        input.ReadBytes(outputBuffer, totalBodySize - 1);
                        input.SkipBytes(FrameParams.CalculatePadding(totalBodySize));
                        output.Add(outputBuffer);
                    }
                }
            }
            catch (Exception ex)
            {
                throw new CorruptedFrameException(ex);
            }
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

        private static byte GetPacketType(byte packetTypeRlp)
        {
            return packetTypeRlp == 128 ? (byte) 0 : packetTypeRlp;
        }
    }
}