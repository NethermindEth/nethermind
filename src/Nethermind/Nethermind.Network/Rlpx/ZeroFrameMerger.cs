// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using DotNetty.Buffers;
using DotNetty.Codecs;
using DotNetty.Common.Utilities;
using DotNetty.Transport.Channels;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Rlpx
{
    public class ZeroFrameMerger(ILogManager logManager) : ByteToMessageDecoder
    {
        private readonly ILogger _logger = logManager?.GetClassLogger<ZeroFrameMerger>() ?? throw new ArgumentNullException(nameof(logManager));

        private ZeroPacket? _zeroPacket;
        private int? _currentContextId;
        private readonly FrameHeaderReader _headerReader = new();

        public override void HandlerRemoved(IChannelHandlerContext context)
        {
            base.HandlerRemoved(context);
            ReleaseInProgressPacket();
        }

        protected override void Decode(IChannelHandlerContext context, IByteBuffer input, List<object> output)
        {
            // Note that each input is a full frame header16|payload that are automatically released by the base class.
            // If the input is not a full and valid frame we can throw as this is an unexpected behaviour from the
            // decoder up the pipeline.

            // Moreover we will never receive more than a full packet in a single input so the input buffer
            // is expected to have no readable bytes after the merging operation.

            if (_logger.IsTrace) _logger.Trace("Merging frames");
            if (input.ReferenceCount != 1)
            {
                throw new IllegalReferenceCountException(input.ReferenceCount);
            }

            FrameHeaderReader.FrameInfo frame = _headerReader.ReadFrameHeader(input);
            bool isFirst = frame.TotalPacketSize.HasValue || _zeroPacket is null;
            if (isFirst)
            {
                if (_zeroPacket is not null)
                {
                    // Offending frame is intentionally not processed: the CorruptedFrameException
                    // propagates up the pipeline and closes the peer connection.
                    ReleaseInProgressPacket();
                    throw new CorruptedFrameException($"{nameof(ZeroFrameMerger)} received a new first chunk before the in-progress packet completed");
                }

                ReadFirstChunk(context, input, frame);
                _currentContextId = frame.TotalPacketSize.HasValue ? frame.ContextId : null;
            }
            else
            {
                if (frame.ContextId != _currentContextId)
                {
                    int? expectedContextId = _currentContextId;
                    ReleaseInProgressPacket();
                    ThrowUnexpectedContextId(frame.ContextId, expectedContextId);
                }

                ReadChunk(input, frame);
            }

            input.SkipBytes(frame.Padding);

            if (_zeroPacket.Content.MaxWritableBytes == 0)
            {
                output.Add(_zeroPacket);
                _zeroPacket = null;
                _currentContextId = null;

                if (input.IsReadable())
                {
                    throw new CorruptedFrameException($"{nameof(ZeroFrameMerger)} received a corrupted frame - {input.ReadableBytes} longer than expected");
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ReadChunk(IByteBuffer input, in FrameHeaderReader.FrameInfo frame)
        {
            if (frame.Size > _zeroPacket.Content.MaxWritableBytes)
            {
                int remainingPacketSize = _zeroPacket.Content.MaxWritableBytes;
                ReleaseInProgressPacket();
                ThrowFrameSizeExceedsRemaining(frame.Size, remainingPacketSize);
            }

            _zeroPacket.Content.EnsureWritable(frame.Size);
            input.ReadBytes(_zeroPacket.Content, frame.Size);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ReadFirstChunk(IChannelHandlerContext context, IByteBuffer input, in FrameHeaderReader.FrameInfo frame)
        {
            ulong rlpPacketType = DecodePacketType(input, out int read);

            if (read > frame.Size)
            {
                ThrowPacketTypeLengthExceedsFrameSize(read, frame.Size);
            }

            if (rlpPacketType > byte.MaxValue)
            {
                ThrowPacketTypeOutOfRange(rlpPacketType);
            }

            input.SkipBytes(read);
            IByteBuffer content;
            if (frame.TotalPacketSize.HasValue)
            {
                int initialContentSize = frame.Size - read;
                int totalContentSize = frame.TotalPacketSize.Value - read;
                content = context.Allocator.Buffer(initialContentSize, totalContentSize);
            }
            else
            {
                content = input.ReadRetainedSlice(frame.Size - read);
            }

            _zeroPacket = new ZeroPacket(content)
            {
                PacketType = (byte)rlpPacketType
            };

            // If not chunked, then we already used a slice of the input,
            // otherwise we need to read into the freshly allocated buffer.
            if (frame.TotalPacketSize.HasValue)
            {
                input.ReadBytes(_zeroPacket.Content, frame.Size - read);
                // do not call Release since the input buffer is managed by
            }
        }

        private void ReleaseInProgressPacket()
        {
            _zeroPacket?.Release();
            _zeroPacket = null;
            _currentContextId = null;
        }

        /// <remarks>
        /// The try/catch lives here rather than in <see cref="ReadFirstChunk"/> so that the caller stays free of an
        /// exception handling region, which RyuJIT refuses to inline.
        /// </remarks>
        private static ulong DecodePacketType(IByteBuffer input, out int read)
        {
            try
            {
                RlpReader reader = new(input.AsSpan());
                ulong packetType = reader.DecodeULong();
                read = reader.Position;
                return packetType;
            }
            catch (Exception exception) when (exception is RlpException or ArgumentOutOfRangeException or IndexOutOfRangeException)
            {
                throw new CorruptedFrameException(exception);
            }
        }

        [DoesNotReturn, StackTraceHidden]
        private static void ThrowFrameSizeExceedsRemaining(int frameSize, int remainingPacketSize)
            => throw new CorruptedFrameException(
                $"{nameof(ZeroFrameMerger)} frame size {frameSize} exceeds remaining packet size {remainingPacketSize}");

        [DoesNotReturn, StackTraceHidden]
        private static void ThrowPacketTypeLengthExceedsFrameSize(int packetTypeLength, int frameSize)
            => throw new CorruptedFrameException(
                $"{nameof(ZeroFrameMerger)} packet type length {packetTypeLength} exceeds frame size {frameSize}");

        [DoesNotReturn, StackTraceHidden]
        private static void ThrowPacketTypeOutOfRange(ulong packetType)
            => throw new CorruptedFrameException(
                $"{nameof(ZeroFrameMerger)} packet type {packetType} does not fit in a byte");

        [DoesNotReturn, StackTraceHidden]
        private static void ThrowUnexpectedContextId(int? contextId, int? expectedContextId)
            => throw new CorruptedFrameException(
                $"{nameof(ZeroFrameMerger)} continuation frame context id {contextId} does not match in-progress packet context id {expectedContextId}");
    }
}
