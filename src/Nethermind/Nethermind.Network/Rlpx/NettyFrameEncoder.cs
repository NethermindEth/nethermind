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
using DotNetty.Buffers;
using DotNetty.Codecs;
using DotNetty.Transport.Channels;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Network.Rlpx
{
    public class NettyFrameEncoder : MessageToByteEncoder<byte[]>
    {
        private readonly ILogger _logger;
        private readonly IFrameCipher _frameCipher;
        private readonly IFrameMacProcessor _frameMacProcessor;
        
        public NettyFrameEncoder(IFrameCipher frameCipher, IFrameMacProcessor frameMacProcessor, ILogManager logManager)
        {
            _frameCipher = frameCipher ?? throw new ArgumentNullException(nameof(frameCipher));
            _frameMacProcessor = frameMacProcessor ?? throw new ArgumentNullException(nameof(frameMacProcessor));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        protected override void Encode(IChannelHandlerContext context, byte[] message, IByteBuffer output)
        {
            if (message.Length % 16 != 0)
            {
                throw new InvalidOperationException($"Frame length should be a multiple of 16");
            }

            if(_logger.IsTrace) _logger.Trace($"Sending frame (before encryption): {message.ToHexString()}");
            _frameCipher.Encrypt(message, 0, 16, message, 0);
            _frameMacProcessor.AddMac(message, 0, 16, true);
            _frameCipher.Encrypt(message, 32, message.Length - 48, message, 32);
            _frameMacProcessor.AddMac(message, 32, message.Length - 48, false);
            if(_logger.IsTrace) _logger.Trace($"Sending frame (after encryption):  {message.ToHexString()}");
            output.WriteBytes(message);
        }
    }
}