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
using System.Linq;
using DotNetty.Buffers;
using DotNetty.Codecs;
using DotNetty.Transport.Channels;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Network.Rlpx
{
    public class NettyFrameDecoder : ByteToMessageDecoder
    {
        private const int MacSize = 16;

        private readonly IFrameCipher _frameCipher;
        private readonly IFrameMacProcessor _frameMacProcessor;
        private readonly ILogger _logger;

        private readonly byte[] _headerBuffer = new byte[32];

        private FrameDecoderState _state = FrameDecoderState.WaitingForHeader;
        private int _totalBodySize;

        public NettyFrameDecoder(IFrameCipher frameCipher, IFrameMacProcessor frameMacProcessor, ILogManager logManager)
        {
            _frameCipher = frameCipher ?? throw new ArgumentNullException(nameof(frameCipher));
            _frameMacProcessor = frameMacProcessor ?? throw new ArgumentNullException(nameof(frameMacProcessor));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        protected override void Decode(IChannelHandlerContext context, IByteBuffer input, List<object> output)
        {
            if (_state == FrameDecoderState.WaitingForHeader)
            {
                if (_logger.IsTrace) _logger.Trace($"Decoding frame header {input.ReadableBytes}");

                if (input.ReadableBytes == 0)
                {
                    if (_logger.IsTrace) _logger.Trace($"{context.Channel.RemoteAddress} sent an empty frame, disconnecting");
                    context.CloseAsync();
                    return;
                }

                if (input.ReadableBytes >= 32)
                {
                    input.ReadBytes(_headerBuffer);
                    if (_logger.IsTrace) _logger.Trace($"Decoding encrypted frame header {_headerBuffer.ToHexString()}");

                    _frameMacProcessor.CheckMac(_headerBuffer, 0, 16, true);
                    _frameCipher.Decrypt(_headerBuffer, 0, 16, _headerBuffer, 0);

                    _totalBodySize = _headerBuffer[0] & 0xFF;
                    _totalBodySize = (_totalBodySize << 8) + (_headerBuffer[1] & 0xFF);
                    _totalBodySize = (_totalBodySize << 8) + (_headerBuffer[2] & 0xFF);
                    _state = FrameDecoderState.WaitingForPayload;

                    int paddingSize = Frame.CalculatePadding(_totalBodySize);
                    if (_logger.IsTrace) _logger.Trace($"Expecting a message {_totalBodySize} + {paddingSize} + 16");
                }
                else
                {
                    if (_logger.IsTrace) _logger.Trace("Waiting for full 32 bytes of the header");
                    return;
                }
            }

            if (_state == FrameDecoderState.WaitingForPayload)
            {
                if (_logger.IsTrace) _logger.Trace($"Decoding payload {input.ReadableBytes}");

                int paddingSize = Frame.CalculatePadding(_totalBodySize);
                int expectedSize = _totalBodySize + paddingSize + MacSize;
                byte[] buffer;
                if (input.ReadableBytes >= expectedSize)
                {
                    buffer = new byte[expectedSize + _headerBuffer.Length];
                    input.ReadBytes(buffer, 32, expectedSize);
                }
                else
                {
                    return;
                }

                if (_logger.IsTrace) _logger.Trace($"Decoding encrypted payload {buffer.ToHexString()}");

                int frameSize = buffer.Length - MacSize - _headerBuffer.Length;
                _frameMacProcessor.CheckMac(buffer, 32, frameSize, false);
                _frameCipher.Decrypt(buffer, 32, frameSize, buffer, 32);

                _headerBuffer.AsSpan().CopyTo(buffer.AsSpan().Slice(0, 32));
                output.Add(buffer);

                if (_logger.IsTrace) _logger.Trace($"Decrypted message {((byte[]) output.Last()).ToHexString()}");

                _state = FrameDecoderState.WaitingForHeader;
            }
        }

        private enum FrameDecoderState
        {
            WaitingForHeader,
            WaitingForPayload
        }
    }
}