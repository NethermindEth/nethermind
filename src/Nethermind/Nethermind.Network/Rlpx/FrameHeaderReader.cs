// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using DotNetty.Codecs;
using Nethermind.Serialization.Rlp;
using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Network.Rlpx
{
    internal class FrameHeaderReader
    {
        private const int HeaderBodyOffset = 3;
        private const int HeaderBodyLength = Frame.HeaderSize - HeaderBodyOffset;

        private int? _currentContextId;

        public byte[] HeaderBytes { get; } = new byte[Frame.HeaderSize];

        public FrameInfo ReadFrameHeader(IByteBuffer input)
        {
            input.ReadBytes(HeaderBytes);
            int frameSize = HeaderBytes[0] & 0xFF;
            frameSize = (frameSize << 8) + (HeaderBytes[1] & 0xFF);
            frameSize = (frameSize << 8) + (HeaderBytes[2] & 0xFF);

            ReadHeaderBody(out int? contextId, out int? totalPacketSize);

            ValidateTotalPacketSize(frameSize, totalPacketSize);

            // A frame that carries a context id but no total packet size continues the packet opened under that same
            // context id, so the comparison has to happen before the field is overwritten.
            bool isChunked = totalPacketSize.HasValue || contextId.HasValue && _currentContextId == contextId && contextId != 0;
            bool isFirst = totalPacketSize.HasValue || !isChunked;
            _currentContextId = contextId;

            return new FrameInfo(isChunked, isFirst, frameSize, totalPacketSize ?? frameSize);
        }

        /// <summary>Decodes the RLP header body of the frame header just read into <see cref="HeaderBytes"/>.</summary>
        /// <remarks>
        /// Kept separate from <see cref="ReadFrameHeader"/> so that the exception handling region covers the RLP
        /// decoding only, rather than also spanning the frame size read and <see cref="ValidateTotalPacketSize"/>.
        /// </remarks>
        /// <exception cref="CorruptedFrameException">The header body is not a well formed RLP sequence.</exception>
        private void ReadHeaderBody(out int? contextId, out int? totalPacketSize)
        {
            try
            {
                RlpReader headerBodyItems = new(HeaderBytes.AsSpan(HeaderBodyOffset, HeaderBodyLength));
                int headerDataLength = headerBodyItems.ReadSequenceLength();
                int remaining = headerBodyItems.Length - headerBodyItems.Position;
                if ((uint)headerDataLength > (uint)remaining)
                {
                    throw new CorruptedFrameException($"Invalid Rlpx header lengths, header body RLP length {headerDataLength} exceeds the {remaining} bytes left in the header");
                }

                int headerDataEnd = headerDataLength + headerBodyItems.Position;
                int numberOfItems = headerBodyItems.PeekNumberOfItemsRemaining(headerDataEnd);
                headerBodyItems.DecodeInt(); // not needed - adaptive IDs - DO NOT COMMENT OUT!!! - decode takes int of the RLP sequence and moves the position
                contextId = numberOfItems > 1 ? headerBodyItems.DecodeInt() : (int?)null;
                totalPacketSize = numberOfItems > 2 ? headerBodyItems.DecodeInt() : (int?)null;
                headerBodyItems.Check(headerDataEnd);
            }
            catch (Exception exception) when (exception is RlpException or ArgumentOutOfRangeException or IndexOutOfRangeException)
            {
                throw new CorruptedFrameException(exception);
            }
        }

        private static void ValidateTotalPacketSize(int frameSize, int? totalPacketSize)
        {
            if (totalPacketSize is not null)
            {
                if (totalPacketSize <= 0 || totalPacketSize > SnappyParameters.MaxSnappyLength)
                {
                    ThrowCorruptedFrameException(frameSize, totalPacketSize.Value);
                }

                if (frameSize > totalPacketSize)
                {
                    ThrowCorruptedFrameException(frameSize, totalPacketSize.Value);
                }
            }

            [DoesNotReturn, StackTraceHidden]
            static void ThrowCorruptedFrameException(int frameSize, int totalPacketSize) => throw new CorruptedFrameException($"Invalid Rlpx header lengths, packet size {totalPacketSize}, frame size {frameSize}");
        }

        internal readonly struct FrameInfo(bool isChunked, bool isFirst, int size, int totalPacketSize)
        {
            public bool IsChunked { get; } = isChunked;
            public bool IsFirst { get; } = isFirst;
            public int Size { get; } = size;
            public int TotalPacketSize { get; } = totalPacketSize;
            public int Padding => Frame.CalculatePadding(Size);

            public int PayloadSize => Size + Padding;
        }
    }
}
