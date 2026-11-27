// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using DotNetty.Codecs;
using DotNetty.Transport.Channels;
using Nethermind.Serialization.Rlp;
using Nethermind.Logging;
using System;
using System.Buffers;

namespace Nethermind.Network.Rlpx
{
    public class ZeroPacketSplitter : MessageToByteEncoder<IByteBuffer>
    {
        private readonly FrameEncoder _encoder;
        private int _pendingInputLength;

        public ZeroPacketSplitter() : this(new FrameEncoder(null, null))
        {
        }

        internal ZeroPacketSplitter(IFrameCipher cipher, IFrameMacProcessor macProcessor) : this(new FrameEncoder(cipher, macProcessor))
        {
        }

        private ZeroPacketSplitter(FrameEncoder encoder) => _encoder = encoder;

        /// <remarks>Unframed encoding is retained for wire-equivalence tests and outbound benchmarks.</remarks>
        internal void DisableFraming() => _encoder.DisableFraming();

        internal void EnableSnappy(ILogManager logManager) => _encoder.EnableSnappy(logManager.GetClassLogger<ZeroSnappyEncoder>());

        public override bool AcceptOutboundMessage(object message)
        {
            if (message is not IByteBuffer input)
            {
                return false;
            }

            // The base encoder calls AllocateBuffer right after this on the same event loop thread,
            // so the input length is recorded here to size the output buffer once.
            _pendingInputLength = input.ReadableBytes;
            return true;
        }

        protected override IByteBuffer AllocateBuffer(IChannelHandlerContext context) =>
            context.Allocator.Buffer(_encoder.GetMaxEncodedLength(_pendingInputLength, _encoder.Mode));

        protected override void Encode(IChannelHandlerContext context, IByteBuffer input, IByteBuffer output)
        {
            FrameEncoder.EncodingMode mode = _encoder.Mode;
            int length = input.ReadableBytes;
            // Grows the output only if Snappy was enabled after it was sized, or when it was not sized for this packet.
            output.EnsureWritable(_encoder.GetMaxEncodedLength(length, mode));
            Span<byte> destination = output.Array.AsSpan(output.ArrayOffset + output.WriterIndex, output.WritableBytes);
            int written = input.HasArray
                ? _encoder.Encode(input.AsSpan(), destination, mode)
                : EncodeCopy(input, destination, mode);
            input.SkipBytes(length);
            output.SetWriterIndex(output.WriterIndex + written);
        }

        /// <summary>Encodes a packet spread over buffer components from a contiguous copy.</summary>
        private int EncodeCopy(IByteBuffer input, Span<byte> destination, FrameEncoder.EncodingMode mode)
        {
            int length = input.ReadableBytes;
            byte[] packet = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                input.GetBytes(input.ReaderIndex, packet, 0, length);
                return _encoder.Encode(packet.AsSpan(0, length), destination, mode);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(packet);
            }
        }
    }
}
