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
using System.Runtime.CompilerServices;
using DotNetty.Buffers;
using DotNetty.Codecs;
using DotNetty.Transport.Channels;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Network.Rlpx
{
    public class ZeroNettyFrameDecoder : ByteToMessageDecoder
    {
        private readonly ILogger _logger;
        private readonly IFrameCipher _cipher;
        private readonly IFrameMacProcessor _authenticator;

        private readonly byte[] _headerBytes = new byte[FrameParams.HeaderSize];
        private readonly byte[] _macBytes = new byte[FrameParams.MacSize];
        private readonly byte[] _frameBlockBytes = new byte[FrameParams.BlockSize];
        private readonly byte[] _decryptedBytes = new byte[FrameParams.BlockSize];

        private FrameDecoderState _state = FrameDecoderState.WaitingForHeader;
        private IByteBuffer _innerBuffer;
        private int _frameSize;
        private int _remainingPayloadBlocks;

        public ZeroNettyFrameDecoder(IFrameCipher frameCipher, IFrameMacProcessor frameMacProcessor, ILogManager logManager)
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
            while (input.ReadableBytes >= FrameParams.BlockSize)
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
                        throw new NotImplementedException($"{nameof(ZeroNettyFrameDecoder)} does not support {_state} state.");
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
            _innerBuffer = context.Allocator.Buffer(FrameParams.HeaderSize + _frameSize);
            _innerBuffer.WriteBytes(_decryptedBytes, 0, FrameParams.HeaderSize);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ReadFrameSize()
        {
            _frameSize = _decryptedBytes[0] & 0xFF;
            _frameSize = (_frameSize << 8) + (_decryptedBytes[1] & 0xFF);
            _frameSize = (_frameSize << 8) + (_decryptedBytes[2] & 0xFF);

            int paddingSize = FrameParams.CalculatePadding(_frameSize);
            _frameSize += paddingSize;
            _remainingPayloadBlocks = _frameSize / FrameParams.BlockSize;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void DecryptHeader()
        {
            _cipher.Decrypt(_headerBytes, 0, FrameParams.BlockSize, _decryptedBytes, 0);
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
            _cipher.Decrypt(_frameBlockBytes, 0, FrameParams.BlockSize, _decryptedBytes, 0);
            _innerBuffer.WriteBytes(_decryptedBytes);
            _remainingPayloadBlocks--;
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
            WaitingForHeaderMac,
            WaitingForPayload,
            WaitingForPayloadMac
        }
    }
}