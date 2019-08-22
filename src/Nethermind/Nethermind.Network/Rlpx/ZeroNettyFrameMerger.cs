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
using DotNetty.Transport.Channels;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Network.Rlpx
{
    public class ZeroNettyFrameMerger : MessageToMessageDecoder<IByteBuffer>
    {
        private const int HeaderSize = 32;
        private const int FrameMacSize = 16;
        private readonly Dictionary<int, int> _currentSizes = new Dictionary<int, int>();
        private readonly ILogger _logger;

        private readonly Dictionary<int, Packet> _packets = new Dictionary<int, Packet>();
        private readonly Dictionary<int, List<byte[]>> _payloads = new Dictionary<int, List<byte[]>>();
        private readonly Dictionary<int, int> _totalPayloadSizes = new Dictionary<int, int>();

        public ZeroNettyFrameMerger(ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        protected override void Decode(IChannelHandlerContext context, IByteBuffer input, List<object> output)
        {
            if (_logger.IsTrace)
            {
                _logger.Trace("Merging frames");
            }

            byte[] header = new byte[16];
            input.ReadBytes(header);
            
            Rlp.ValueDecoderContext headerBodyItems = header.Slice(3, 13).AsRlpValueContext();
            int headerDataEnd = headerBodyItems.ReadSequenceLength() + headerBodyItems.Position;
            int numberOfItems = headerBodyItems.ReadNumberOfItemsRemaining(headerDataEnd);
            headerBodyItems.DecodeInt(); // not needed - adaptive IDs - DO NOT COMMENT OUT!!! - decode takes int of the RLP sequence and moves the position
            int? contextId = numberOfItems > 1 ? headerBodyItems.DecodeInt() : (int?) null;
            int? totalPacketSize = numberOfItems > 2 ? headerBodyItems.DecodeInt() : (int?) null;

            bool isChunked = totalPacketSize.HasValue || contextId.HasValue && _currentSizes.ContainsKey(contextId.Value);
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
                    _currentSizes[contextId.Value] = 0;
                    _totalPayloadSizes[contextId.Value] = totalPacketSize.Value - 1; // packet type data size
                    _packets[contextId.Value] = new Packet("???", input.ReadByte(), new byte[_totalPayloadSizes[contextId.Value]]); // adaptive IDs
                    _payloads[contextId.Value] = new List<byte[]>();
                }

                int packetTypeDataSize = isFirstChunk ? 1 : 0;
                int frameSize = input.ReadableBytes - packetTypeDataSize;
                
                byte[] payload = new byte[frameSize];
                input.ReadBytes(payload);
                _payloads[contextId.Value].Add(payload);
                _currentSizes[contextId.Value] += frameSize;
                if (_currentSizes[contextId.Value] >= _totalPayloadSizes[contextId.Value])
                {
                    int padding = _currentSizes[contextId.Value] - _totalPayloadSizes[contextId.Value];
                    Packet packet = _packets[contextId.Value];
                    int offset = 0;
                    int frameCount = _payloads[contextId.Value].Count;
                    for (int i = 0; i < frameCount; i++)
                    {
                        int length = _payloads[contextId.Value][i].Length - (i == frameCount - 1 ? padding : 0);
                        Buffer.BlockCopy(_payloads[contextId.Value][i], 0, packet.Data, offset, length);
                        offset += length;
                    }

                    output.Add(packet);
                    _currentSizes.Remove(contextId.Value);
                    _totalPayloadSizes.Remove(contextId.Value);
                    _payloads.Remove(contextId.Value);
                    _packets.Remove(contextId.Value);
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
                input.SkipBytes(FramePadding.Calculate16(totalBodySize));
                output.Add(outputBuffer);
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
            return packetTypeRlp == 128 ? (byte)0 : packetTypeRlp;
        }
    }
}