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
        private int? _currentContextId;

        public byte[] HeaderBytes { get; } = new byte[Frame.HeaderSize];

        public FrameInfo ReadFrameHeader(IByteBuffer input)
        {
            input.ReadBytes(HeaderBytes);
            int frameSize = HeaderBytes[0] & 0xFF;
            frameSize = (frameSize << 8) + (HeaderBytes[1] & 0xFF);
            frameSize = (frameSize << 8) + (HeaderBytes[2] & 0xFF);

            Rlp.ValueDecoderContext headerBodyItems = HeaderBytes.AsSpan(3, 13).AsRlpValueContext();
            int headerDataEnd = headerBodyItems.ReadSequenceLength() + headerBodyItems.Position;
            int numberOfItems = headerBodyItems.PeekNumberOfItemsRemaining(headerDataEnd);
            headerBodyItems.DecodeInt(); // not needed - adaptive IDs - DO NOT COMMENT OUT!!! - decode takes int of the RLP sequence and moves the position
            int? contextId = numberOfItems > 1 ? headerBodyItems.DecodeInt() : (int?)null;
            _currentContextId = contextId;
            int? totalPacketSize = numberOfItems > 2 ? headerBodyItems.DecodeInt() : (int?)null;

            ValidateTotalPacketSize(frameSize, totalPacketSize);

            bool isChunked = totalPacketSize.HasValue || contextId.HasValue && _currentContextId == contextId && contextId != 0;
            bool isFirst = totalPacketSize.HasValue || !isChunked;

            headerBodyItems.Check(headerDataEnd);
            return new FrameInfo(isChunked, isFirst, frameSize, totalPacketSize ?? frameSize);
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
            static void ThrowCorruptedFrameException(int frameSize, int totalPacketSize)
            {
                throw new CorruptedFrameException($"Invalid Rlpx header lengths, packet size {totalPacketSize}, frame size {frameSize}");
            }
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
