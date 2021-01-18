//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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
            int numberOfItems = headerBodyItems.ReadNumberOfItemsRemaining(headerDataEnd);
            headerBodyItems.DecodeInt(); // not needed - adaptive IDs - DO NOT COMMENT OUT!!! - decode takes int of the RLP sequence and moves the position
            int? contextId = numberOfItems > 1 ? headerBodyItems.DecodeInt() : (int?) null;
            _currentContextId = contextId;
            int? totalPacketSize = numberOfItems > 2 ? headerBodyItems.DecodeInt() : (int?) null;

            bool isChunked = totalPacketSize.HasValue || contextId.HasValue && _currentContextId == contextId && contextId != 0;
            bool isFirst = totalPacketSize.HasValue || !isChunked;
            return new FrameInfo(isChunked, isFirst, frameSize, totalPacketSize ?? frameSize);
        }
        
        internal struct FrameInfo
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
