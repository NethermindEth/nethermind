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
using System.Runtime.CompilerServices;
using DotNetty.Buffers;
using DotNetty.Codecs;
using DotNetty.Transport.Channels;
using Nethermind.Logging;

namespace Nethermind.Network.Rlpx
{
    public class ZeroFrameDecoder : ByteToMessageDecoder
    {
        private readonly ILogger _logger;
        private readonly IFrameCipher _cipher;
        private readonly IFrameMacProcessor _authenticator;

        private readonly byte[] _headerBytes = new byte[Frame.HeaderSize];
        private readonly byte[] _macBytes = new byte[Frame.MacSize];
        private readonly byte[] _frameBlockBytes = new byte[Frame.BlockSize];
        private readonly byte[] _decryptedBytes = new byte[Frame.BlockSize];

        private FrameDecoderState _state = FrameDecoderState.WaitingForHeader;
        private IByteBuffer _innerBuffer;
        private int _frameSize;
        private int _remainingPayloadBlocks;

        public ZeroFrameDecoder(IFrameCipher frameCipher, IFrameMacProcessor frameMacProcessor, ILogManager logManager)
        {
            _cipher = frameCipher ?? throw new ArgumentNullException(nameof(frameCipher));
            _authenticator = frameMacProcessor ?? throw new ArgumentNullException(nameof(frameMacProcessor));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        protected override void Decode(IChannelHandlerContext context, IByteBuffer input, List<object> output)
        {
          
            // Note that ByteToMessageDecoder handles input.Release calls for us.
            // In fact, we receive here a potentially surviving _internalBuffer of the base class
            // that is being built by its cumulator.
            
            // Output buffers that we create will be released by the next handler in the pipeline.
            while (input.ReadableBytes >= Frame.BlockSize)
            {
                switch (_state)
                {
                    case FrameDecoderState.WaitingForHeader:
                    {
                        ReadHeader(input);
                        _state = FrameDecoderState.WaitingForHeaderMac;
                        break;
                    }
                    case FrameDecoderState.WaitingForHeaderMac:
                    {
                        AuthenticateHeader(input);
                        DecryptHeader();
                        ReadFrameSize();
                        AllocateFrameBuffer(context); // it will be released by the next handler in the pipeline
                        _state = FrameDecoderState.WaitingForPayload;
                        break;
                    }
                    case FrameDecoderState.WaitingForPayload:
                    {
                        ProcessOneBlock(input);
                        if (_remainingPayloadBlocks == 0)
                        {
                            _state = FrameDecoderState.WaitingForPayloadMac;
                        }
                        break;
                    }
                    case FrameDecoderState.WaitingForPayloadMac:
                    {
                        AuthenticatePayload(input);
                        PassFrame(output);
                        _state = FrameDecoderState.WaitingForHeader;
                        break;
                    }
                    default:
                        throw new NotImplementedException($"{nameof(ZeroFrameDecoder)} does not support {_state} state.");
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void PassFrame(List<object> output)
        {
            output.Add(_innerBuffer);
            _innerBuffer = null;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ReadHeader(IByteBuffer input)
        {
            input.ReadBytes(_headerBytes);
            _authenticator.UpdateIngressMac(_headerBytes, true);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void AllocateFrameBuffer(IChannelHandlerContext context)
        {
            _innerBuffer = context.Allocator.Buffer(Frame.HeaderSize + _frameSize);
            _innerBuffer.WriteBytes(_decryptedBytes, 0, Frame.HeaderSize);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ReadFrameSize()
        {
            _frameSize = _decryptedBytes[0] & 0xFF;
            _frameSize = (_frameSize << 8) + (_decryptedBytes[1] & 0xFF);
            _frameSize = (_frameSize << 8) + (_decryptedBytes[2] & 0xFF);

            int paddingSize = Frame.CalculatePadding(_frameSize);
            _frameSize += paddingSize;
            _remainingPayloadBlocks = _frameSize / Frame.BlockSize;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void DecryptHeader()
        {
            _cipher.Decrypt(_headerBytes, 0, Frame.BlockSize, _decryptedBytes, 0);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void AuthenticateHeader(IByteBuffer input)
        {
            input.ReadBytes(_macBytes);
            bool isValidMac = _authenticator.CheckMac(_macBytes, true);
            if (!isValidMac)
            {
                throw new CorruptedFrameException("Sender delivered a frame with an invalid header MAC");
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void AuthenticatePayload(IByteBuffer input)
        {
            input.ReadBytes(_macBytes);
            bool isValidMac = _authenticator.CheckMac(_macBytes, false);
            if (!isValidMac)
            {
                throw new CorruptedFrameException("Sender delivered a frame with an invalid payload MAC");
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ProcessOneBlock(IByteBuffer input)
        {
            input.ReadBytes(_frameBlockBytes);
            _authenticator.UpdateIngressMac(_frameBlockBytes, false);
            _cipher.Decrypt(_frameBlockBytes, 0, Frame.BlockSize, _decryptedBytes, 0);
            _innerBuffer.WriteBytes(_decryptedBytes);
            _remainingPayloadBlocks--;
        }

        private enum FrameDecoderState
        {
            WaitingForHeader,
            WaitingForHeaderMac,
            WaitingForPayload,
            WaitingForPayloadMac
        }
    }
}