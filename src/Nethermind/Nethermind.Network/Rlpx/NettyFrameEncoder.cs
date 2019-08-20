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
        
        public NettyFrameEncoder(IFrameCipher frameCipher, IFrameMacProcessor frameMacProcessor, ILogger logger)
        {
            _frameCipher = frameCipher ?? throw new ArgumentNullException(nameof(frameCipher));
            _frameMacProcessor = frameMacProcessor ?? throw new ArgumentNullException(nameof(frameMacProcessor));
            _logger = logger;
        }

        protected override void Encode(IChannelHandlerContext context, byte[] message, IByteBuffer output)
        {
//            string expected = "0000e1c18000000000000000000000000000000000000000000000000000000007c604f04ef90243f9023ff901f9a0ff483e972a04a9a62bb4b7d04ae403c615604e4090521ecc5bb7af67f71be09ca01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d493479400000042020080a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421ee2100092104b901427600fe0100fe0100fe0100be010024830f424080833d090080010a9883010203a02ba5557a4c62a513c7e56d1bf13373e0da6bec016755483e91589fe1c6d212e2880025252403e8f840df800182520852bf011001808080807e200004c08000000000000000000000000000000000000000000000000000000000000000";
//            string result__ = "0000e1c18000000000000000000000000000000000000000000000000000000001c604f04ef90243f9023ff901f9a0ff483e972a04a9a62bb4b7d04ae403c615604e4090521ecc5bb7af67f71be09ca01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d493479400000042020080a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421ee2100092104b901427600fe0100fe0100fe0100be010024830f424080833d090080010a9883010203a02ba5557a4c62a513c7e56d1bf13373e0da6bec016755483e91589fe1c6d212e2880025252403e8f840df800182520852bf011001808080807e200004c08000000000000000000000000000000000000000000000000000000000000000";
//            if (message.ToHexString() != expected)
//            {
//                throw new Exception(message.ToHexString());
//            }
            
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