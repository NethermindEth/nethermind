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
using DotNetty.Codecs;
using DotNetty.Transport.Channels;
using Nethermind.Logging;
using Snappy;

namespace Nethermind.Network.Rlpx
{
    public class ZeroSnappyEncoder : MessageToByteEncoder<IByteBuffer>
    {
        private readonly ILogger _logger;

        public ZeroSnappyEncoder(ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger<ZeroSnappyEncoder>() ?? throw new ArgumentNullException(nameof(logManager));
        }

        protected override void Encode(IChannelHandlerContext context, IByteBuffer input, IByteBuffer output)
        {
            byte packetType = input.ReadByte();

            output.EnsureWritable(1, true);
            output.WriteByte(packetType);

            output.EnsureWritable(SnappyCodec.GetMaxCompressedLength(input.ReadableBytes), true);
            if (_logger.IsTrace) _logger.Trace($"Compressing with Snappy a message of length {input.ReadableBytes}");
            int length = SnappyCodec.Compress(
                input.Array,
                input.ArrayOffset + input.ReaderIndex,
                input.ReadableBytes,
                output.Array,
                output.ArrayOffset + output.WriterIndex);
            
            input.SetReaderIndex(input.ReaderIndex + input.ReadableBytes);
            output.SetWriterIndex(output.WriterIndex + length);
        }
    }
}
