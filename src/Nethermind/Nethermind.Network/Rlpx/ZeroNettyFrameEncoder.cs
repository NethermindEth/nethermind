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
using DotNetty.Common.Utilities;
using DotNetty.Transport.Channels;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Network.Rlpx
{
    public class ZeroNettyFrameEncoder : MessageToByteEncoder<IByteBuffer>
    {
        private readonly ILogger _logger;
        private readonly IFrameCipher _frameCipher;
        private readonly IFrameMacProcessor _frameMacProcessor;
        
        public int MaxFrameSize = FrameParams.DefaultMaxFrameSize;

        public void DisableFraming()
        {
            MaxFrameSize = int.MaxValue;
        }
        
        public ZeroNettyFrameEncoder(IFrameCipher frameCipher, IFrameMacProcessor frameMacProcessor, ILogManager logManager)
        {
            _frameCipher = frameCipher ?? throw new ArgumentNullException(nameof(frameCipher));
            _frameMacProcessor = frameMacProcessor ?? throw new ArgumentNullException(nameof(frameMacProcessor));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        private byte[] _encryptBuffer = new byte[FrameParams.FrameBlockSize];
        private byte[] _macBuffer = new byte[16];

        protected override void Encode(IChannelHandlerContext context, IByteBuffer input, IByteBuffer output)
        {
            while (input.ReadableBytes > 0)
            {
                int frameLength = Math.Min(MaxFrameSize, input.ReadableBytes - 16);
                
                if (input.ReadableBytes % FrameParams.FrameBlockSize != 0)
                {
                    throw new InvalidOperationException($"Frame length should be a multiple of 16");
                }

                // header | header MAC | payload | payload MAC
                output.MakeSpace(16 + 16 + frameLength + 16, "encoder");

                input.ReadBytes(_encryptBuffer);
                _frameCipher.Encrypt(_encryptBuffer, 0, 16, _encryptBuffer, 0);
                output.WriteBytes(_encryptBuffer);

                _frameMacProcessor.AddMac(_encryptBuffer, 0, 16, _macBuffer, 0, true);
                output.WriteBytes(_macBuffer);
                
                for (int i = 0; i < frameLength / FrameParams.FrameBlockSize; i++)
                {
                    input.ReadBytes(_encryptBuffer);
                    _frameCipher.Encrypt(_encryptBuffer, 0, 16, _encryptBuffer, 0);
                    _frameMacProcessor.EgressUpdate(_encryptBuffer);
                    output.WriteBytes(_encryptBuffer);
                }

                _frameMacProcessor.CalculateMac(_macBuffer);
                output.WriteBytes(_macBuffer);
            }
        }
    }
}