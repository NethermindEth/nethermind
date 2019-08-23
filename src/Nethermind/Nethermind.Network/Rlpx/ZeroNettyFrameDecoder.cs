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
using System.Net.Sockets;
using DotNetty.Buffers;
using DotNetty.Codecs;
using DotNetty.Transport.Channels;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Network.Rlpx
{
    public class ZeroNettyFrameDecoder : ByteToMessageDecoder
    {
        private const int HeaderSize = 16;
        private const int MacSize = 16;

        private readonly IFrameCipher _frameCipher;
        private readonly IFrameMacProcessor _frameMacProcessor;
        private readonly ILogger _logger;

        private readonly byte[] _headerBuffer = new byte[HeaderSize + MacSize];

        private FrameDecoderState _state = FrameDecoderState.WaitingForHeader;
        private int _frameSize;

        public ZeroNettyFrameDecoder(IFrameCipher frameCipher, IFrameMacProcessor frameMacProcessor, ILogManager logManager)
        {
            _frameCipher = frameCipher ?? throw new ArgumentNullException(nameof(frameCipher));
            _frameMacProcessor = frameMacProcessor ?? throw new ArgumentNullException(nameof(frameMacProcessor));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        protected override void Decode(IChannelHandlerContext context, IByteBuffer input, List<object> output)
        {
            IByteBuffer buffer = null;
            while (input.ReadableBytes > 0)
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

                    if (input.ReadableBytes >= HeaderSize + MacSize)
                    {
                        input.ReadBytes(_headerBuffer);
                        if (_logger.IsTrace) _logger.Trace($"Decoding encrypted frame header {_headerBuffer.ToHexString()}");

                        _frameMacProcessor.CheckMac(_headerBuffer, 0, 16, true);
                        _frameCipher.Decrypt(_headerBuffer, 0, 16, _headerBuffer, 0);

                        _frameSize = _headerBuffer[0] & 0xFF;
                        _frameSize = (_frameSize << 8) + (_headerBuffer[1] & 0xFF);
                        _frameSize = (_frameSize << 8) + (_headerBuffer[2] & 0xFF);
                        int paddingSize = FramePadding.Calculate16(_frameSize);
                        _frameSize += paddingSize;
                        _state = FrameDecoderState.WaitingForPayload;

                        if (_logger.IsTrace) _logger.Trace($"Expecting a message {_frameSize - paddingSize} + {paddingSize} + 16");
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
                    
                    if (input.ReadableBytes < _frameSize + MacSize)
                    {
                        return;
                    }
                    
                    if (buffer == null)
                    {
                        buffer = PooledByteBufferAllocator.Default.Buffer(HeaderSize + _frameSize);
                        buffer.Retain();
                    }
                    
                    buffer.MakeSpace(HeaderSize + _frameSize);
                    buffer.WriteBytes(_headerBuffer, 0, HeaderSize);

                    _frameMacProcessor.CheckMac(input.Array, input.ArrayOffset + input.ReaderIndex, _frameSize, false);
                    _frameCipher.Decrypt(input.Array, input.ArrayOffset + input.ReaderIndex, _frameSize, buffer.Array, buffer.ArrayOffset + buffer.WriterIndex);

                    input.SetReaderIndex(input.ReaderIndex + _frameSize + MacSize);
                    buffer.SetWriterIndex(buffer.WriterIndex + _frameSize);
                    output.Add(buffer);
                    
                    _state = FrameDecoderState.WaitingForHeader;
                }
            }
        }

        public override void ExceptionCaught(IChannelHandlerContext context, Exception exception)
        {
            _logger.Warn(exception.ToString());
            
            //In case of SocketException we log it as debug to avoid noise
            if (exception is SocketException)
            {
                if (_logger.IsTrace) _logger.Trace($"Frame decoding failed (SocketException): {exception}");
            }
            else if (exception.Message?.Contains("MAC mismatch") ?? false)
            {
                if (_logger.IsTrace) _logger.Trace($"{GetType().Name} MAC mismatch error: {exception}");
            }
            else
            {
                if (_logger.IsDebug) _logger.Debug($"{GetType().Name} error: {exception}");
            }

            base.ExceptionCaught(context, exception);
        }
        
        private enum FrameDecoderState
        {
            WaitingForHeader,
            WaitingForPayload
        }
    }
}