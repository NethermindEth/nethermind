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
using DotNetty.Codecs;
using DotNetty.Transport.Channels;
using Nevermind.Core;
using Nevermind.Core.Encoding;
using Nevermind.Core.Extensions;

namespace Nevermind.Network.Rlpx
{
    public class NettyFrameMerger : MessageToMessageDecoder<byte[]>
    {
        private const int HeaderSize = 32;
        private const int FrameMacSize = 16;
        private readonly Dictionary<int, int> _currentSizes = new Dictionary<int, int>();
        private readonly ILogger _logger;

        private readonly Dictionary<int, Packet> _packets = new Dictionary<int, Packet>();
        private readonly Dictionary<int, List<byte[]>> _payloads = new Dictionary<int, List<byte[]>>();
        private readonly Dictionary<int, int> _totalPayloadSizes = new Dictionary<int, int>();

        public NettyFrameMerger(ILogger logger)
        {
            _logger = logger;
        }

        protected override void Decode(IChannelHandlerContext context, byte[] input, List<object> output)
        {
            object[] headerBodyItems = (object[])Rlp.Decode(new Rlp(input.Slice(3, 13)), RlpBehaviors.AllowExtraData);
            int protocolType = ((byte[])headerBodyItems[0]).ToInt32();
            int? contextId = headerBodyItems.Length > 1 ? ((byte[])headerBodyItems[1]).ToInt32() : (int?)null;
            int? totalPacketSize = headerBodyItems.Length > 2 ? ((byte[])headerBodyItems[2]).ToInt32() : (int?)null;

            bool isChunked = totalPacketSize.HasValue
                             || contextId.HasValue && _currentSizes.ContainsKey(contextId.Value);
            if (isChunked)
            {
                bool isFirstChunk = totalPacketSize.HasValue;
                if (isFirstChunk)
                {
                    _currentSizes[contextId.Value] = 0;
                    _totalPayloadSizes[contextId.Value] = totalPacketSize.Value - 1; // packet type data size
                    _packets[contextId.Value] = new Packet(protocolType, GetPacketType(input), new byte[_totalPayloadSizes[contextId.Value]]);
                    _payloads[contextId.Value] = new List<byte[]>();
                }

                int packetTypeDataSize = isFirstChunk ? 1 : 0;
                int frameSize = input.Length - HeaderSize - FrameMacSize - packetTypeDataSize;
                _payloads[contextId.Value].Add(input.Slice(32 + packetTypeDataSize, frameSize));
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
                int totalBodySize = input[0] & 0xFF;
                totalBodySize = (totalBodySize << 8) + (input[1] & 0xFF);
                totalBodySize = (totalBodySize << 8) + (input[2] & 0xFF);
                output.Add(new Packet(protocolType, GetPacketType(input), input.Slice(1 + 32, totalBodySize - 1)));
            }
        }

        public override void ExceptionCaught(IChannelHandlerContext context, Exception exception)
        {
            _logger.Error($"{GetType().Name} exception", exception);
            base.ExceptionCaught(context, exception);
        }

        private static int GetPacketType(byte[] input)
        {
            Rlp packetTypeRlp = new Rlp(input.Slice(32, 1));
            int packetType = ((byte[])Rlp.Decode(packetTypeRlp)).ToInt32();
            return packetType;
        }
    }
}