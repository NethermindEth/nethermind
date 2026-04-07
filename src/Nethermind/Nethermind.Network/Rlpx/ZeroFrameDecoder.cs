// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using DotNetty.Buffers;
using DotNetty.Codecs;
using DotNetty.Transport.Channels;
using Nethermind.Core.Extensions;

namespace Nethermind.Network.Rlpx
{
    public class ZeroFrameDecoder(IFrameCipher frameCipher, FrameMacProcessor frameMacProcessor)
        : ByteToMessageDecoder
    {
        // 12 MiB: generous upper bound for devp2p frames. Snap responses can reach ~3 MiB,
        // but block bodies and receipts can be larger. The cap is defense-in-depth against
        // OOM from malicious peers sending oversized frames — not a protocol-level limit.
        public readonly static int DefaultMaxInboundFrameSize = (int)12.MiB;
        private readonly IFrameCipher _cipher = frameCipher ?? throw new ArgumentNullException(nameof(frameCipher));
        private readonly FrameMacProcessor _authenticator = frameMacProcessor ?? throw new ArgumentNullException(nameof(frameMacProcessor));
        private readonly int _maxFrameSize = DefaultMaxInboundFrameSize;

        private readonly byte[] _headerBytes = new byte[Frame.HeaderSize];
        private readonly byte[] _macBytes = new byte[Frame.MacSize];
        private readonly byte[] _frameBlockBytes = new byte[Frame.BlockSize];
        private readonly byte[] _decryptedBytes = new byte[Frame.BlockSize];

        private FrameDecoderState _state = FrameDecoderState.WaitingForHeader;
        private IByteBuffer? _innerBuffer;
        private int _frameSize;
        private int _remainingPayloadBlocks;

        public ZeroFrameDecoder(
            IFrameCipher frameCipher,
            FrameMacProcessor frameMacProcessor,
            int maxFrameSize)
            : this(frameCipher, frameMacProcessor)
        {
            _maxFrameSize = maxFrameSize;
        }

        public override void HandlerRemoved(IChannelHandlerContext context)
        {
            base.HandlerRemoved(context);
            _innerBuffer?.Release();
        }

        protected override void Decode(IChannelHandlerContext context, IByteBuffer input, List<object> output)
        {

            // Note that ByteToMessageDecoder handles input.Release calls for us.
            // In fact, we receive here a potentially surviving _internalBuffer of the base class
            // that is being built by its accumulator.

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
                        throw new NotSupportedException($"{nameof(ZeroFrameDecoder)} does not support {_state} state.");
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
            int payloadSize = _decryptedBytes[0] & 0xFF;
            payloadSize = (payloadSize << 8) + (_decryptedBytes[1] & 0xFF);
            payloadSize = (payloadSize << 8) + (_decryptedBytes[2] & 0xFF);

            if (payloadSize > _maxFrameSize)
                ThrowFrameTooLarge(payloadSize, _maxFrameSize);

            int paddingSize = Frame.CalculatePadding(payloadSize);
            _frameSize = payloadSize + paddingSize;
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
            if (!_authenticator.CheckMac(_macBytes, true))
                ThrowInvalidMac("header");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void AuthenticatePayload(IByteBuffer input)
        {
            input.ReadBytes(_macBytes);
            if (!_authenticator.CheckMac(_macBytes, false))
                ThrowInvalidMac("payload");
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

        [DoesNotReturn, StackTraceHidden]
        private static void ThrowFrameTooLarge(int payloadSize, int maxFrameSize)
            => throw new CorruptedFrameException($"Frame payload too large: {payloadSize} bytes, max {maxFrameSize} bytes");

        [DoesNotReturn, StackTraceHidden]
        private static void ThrowInvalidMac(string section)
            => throw new CorruptedFrameException($"Sender delivered a frame with an invalid {section} MAC");
    }
}
