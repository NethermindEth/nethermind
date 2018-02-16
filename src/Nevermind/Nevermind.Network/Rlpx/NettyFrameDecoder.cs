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

using System.Collections.Generic;
using DotNetty.Buffers;
using DotNetty.Codecs;
using DotNetty.Transport.Channels;
using Nevermind.Core.Extensions;

namespace Nevermind.Network.Rlpx
{
    public class NettyFrameDecoder : ByteToMessageDecoder
    {
        private const int MacSize = 16;

        private readonly IFrameCipher _frameCipher;

        private readonly IFrameMacProcessor _frameMacProcessor;

        private readonly byte[] _headerBuffer = new byte[32];

        private FrameDecoderState _state = FrameDecoderState.WaitingForHeader;
        private int _totalBodySize;

        public NettyFrameDecoder(IFrameCipher frameCipher, IFrameMacProcessor frameMacProcessor)
        {
            _frameCipher = frameCipher;
            _frameMacProcessor = frameMacProcessor;
        }

        protected override void Decode(IChannelHandlerContext context, IByteBuffer input, List<object> output)
        {
            if (_state == FrameDecoderState.WaitingForHeader)
            {
                if (input.ReadableBytes >= 32)
                {
                    input.ReadBytes(_headerBuffer);
                    _frameMacProcessor.CheckMac(_headerBuffer, 0, 16, true);
                    _frameCipher.Decrypt(_headerBuffer, 0, 16, _headerBuffer, 0);

                    _totalBodySize = _headerBuffer[0] & 0xFF;
                    _totalBodySize = (_totalBodySize << 8) + (_headerBuffer[1] & 0xFF);
                    _totalBodySize = (_totalBodySize << 8) + (_headerBuffer[2] & 0xFF);
                    _state = FrameDecoderState.WaitingForPayload;
                }
                else
                {
                    return;
                }
            }

            if (_state == FrameDecoderState.WaitingForPayload)
            {
                int paddingSize = 16 - _totalBodySize % 16;
                if (paddingSize == 16)
                {
                    paddingSize = 0;
                }
                byte[] buffer = new byte[_totalBodySize + paddingSize + MacSize];
                if (input.ReadableBytes >= buffer.Length)
                {
                    input.ReadBytes(buffer);
                }
                else
                {
                    return;
                }

                int frameSize = buffer.Length - MacSize;
                _frameMacProcessor.CheckMac(buffer, 0, frameSize, false);
                _frameCipher.Decrypt(buffer, 0, frameSize, buffer, 0);

                output.Add(Bytes.Concat(_headerBuffer, buffer));
            }
        }

        private enum FrameDecoderState
        {
            WaitingForHeader,
            WaitingForPayload
        }
    }
}