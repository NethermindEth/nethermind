// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using DotNetty.Buffers;
using DotNetty.Codecs;
using DotNetty.Common.Utilities;
using DotNetty.Transport.Channels;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Rlpx
{
    public class ZeroFrameMerger(ILogManager logManager) : MessageToMessageDecoder<IByteBuffer>
    {
        private readonly ILogger _logger = logManager?.GetClassLogger<ZeroFrameMerger>() ?? throw new ArgumentNullException(nameof(logManager));

        private NettyBufferPacket? _zeroPacket;
        private readonly FrameMerger _merger = new();

        public override void HandlerRemoved(IChannelHandlerContext context)
        {
            base.HandlerRemoved(context);
            ReleaseInProgressPacket();
        }

        protected override void Decode(IChannelHandlerContext context, IByteBuffer input, List<object> output)
        {
            // ZeroFrameDecoder emits one complete authenticated frame per message.
            if (_logger.IsTrace) _logger.Trace("Merging frames");
            if (input.ReferenceCount != 1)
            {
                throw new IllegalReferenceCountException(input.ReferenceCount);
            }

            try
            {
                DecodeFrame(context, input, output);
            }
            catch (InvalidFrameException exception)
            {
                ReleaseInProgressPacket();
                throw exception.ToCorruptedFrameException();
            }
            catch
            {
                // Storing a chunk can fail after the merger accepted it, so both halves drop the packet.
                ReleaseInProgressPacket();
                throw;
            }
        }

        private void DecodeFrame(IChannelHandlerContext context, IByteBuffer input, List<object> output)
        {
            MergedFrame frame = _merger.Read(input.AsSpan());
            int contentIndex = input.ReaderIndex + frame.ContentOffset;
            switch (frame.Kind)
            {
                case MergedFrameKind.SinglePacket:
                    input.SetIndex(contentIndex, contentIndex + frame.ContentLength);
                    // The message decoder releases its input after Decode; the packet owns the retained reference.
                    output.Add(new NettyBufferPacket((IByteBuffer)input.Retain()) { PacketType = frame.PacketType });
                    return;
                case MergedFrameKind.FirstChunk:
                    _zeroPacket = new NettyBufferPacket(context.Allocator.Buffer(frame.ContentLength, frame.TotalContentLength))
                    {
                        PacketType = frame.PacketType
                    };
                    break;
            }

            _zeroPacket!.Buffer.EnsureWritable(frame.ContentLength);
            _zeroPacket.Buffer.WriteBytes(input, contentIndex, frame.ContentLength);
            input.SkipBytes(input.ReadableBytes);

            if (frame.IsComplete)
            {
                output.Add(_zeroPacket);
                _zeroPacket = null;
            }
        }

        private void ReleaseInProgressPacket()
        {
            _zeroPacket?.Release();
            _zeroPacket = null;
            _merger.Reset();
        }
    }
}
