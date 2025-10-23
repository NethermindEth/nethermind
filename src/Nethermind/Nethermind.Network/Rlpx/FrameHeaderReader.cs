// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using DotNetty.Buffers;
using Nethermind.Serialization.Rlp;

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

            bool isChunked = totalPacketSize.HasValue || contextId.HasValue && _currentContextId == contextId && contextId != 0;
            bool isFirst = totalPacketSize.HasValue || !isChunked;
            return new FrameInfo(isChunked, isFirst, frameSize, totalPacketSize ?? frameSize);
        }

        internal readonly struct FrameInfo
        {
            public FrameInfo(bool isChunked, bool isFirst, int size, int totalPacketSize)
            {
                IsChunked = isChunked;
                IsFirst = isFirst;
                Size = size;
                TotalPacketSize = totalPacketSize;
            }

            public bool IsChunked { get; }
            public bool IsFirst { get; }
            public int Size { get; }
            public int TotalPacketSize { get; }
            public int Padding => Frame.CalculatePadding(Size);

            public int PayloadSize => Size + Padding;
        }
    }
}
