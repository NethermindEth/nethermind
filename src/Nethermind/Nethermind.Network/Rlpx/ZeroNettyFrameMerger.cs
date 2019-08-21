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

        protected override void Decode(IChannelHandlerContext context, IByteBuffer byteBuffer, List<object> output)
        {
            throw new NotImplementedException();
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

        private static int GetPacketType(byte[] input)
        {
            int packetTypeRlp = input[32];
            return packetTypeRlp == 128 ? 0 : packetTypeRlp;
        }
    }
}