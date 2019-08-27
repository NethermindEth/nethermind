﻿/*
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
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Snappy;

namespace Nethermind.Network.Rlpx
{
    public class SnappyDecoder : MessageToMessageDecoder<Packet>
    {
        private readonly ILogger _logger;

        public SnappyDecoder(ILogger logger)
        {
            _logger = logger;
        }

        protected override void Decode(IChannelHandlerContext context, Packet message, List<object> output)
        {
            if (SnappyCodec.GetUncompressedLength(message.Data) > SnappyParameters.MaxSnappyLength)
            {
                throw new Exception("Max message size exceeeded"); // TODO: disconnect here
            }

            if (message.Data.Length > SnappyParameters.MaxSnappyLength / 4)
            {
                if (_logger.IsWarn) _logger.Warn($"Big Snappy message of length {message.Data.Length}");
            }
            else
            {
                if (_logger.IsTrace) _logger.Trace($"Uncompressing with Snappy a message of length {message.Data.Length}");
            }

            try
            {
                message.Data = SnappyCodec.Uncompress(message.Data);
            }
            catch
            {
                _logger.Error($"{message.Data.ToHexString()}");
                throw;
            }
            
            output.Add(message);
        }
    }
}