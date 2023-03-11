// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
                throw new Exception("Max message size exceeded");
            }

            if (message.Data.Length > SnappyParameters.MaxSnappyLength / 4)
            {
                if (_logger.IsWarn) _logger.Warn($"Big Snappy message of length {message.Data.Length}");
            }
            else
            {
                if (_logger.IsTrace) _logger.Trace($"Decompressing with Snappy a message of length {message.Data.Length}");
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
