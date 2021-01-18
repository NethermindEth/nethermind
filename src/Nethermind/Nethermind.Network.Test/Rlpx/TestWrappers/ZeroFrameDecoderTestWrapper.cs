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

using System.Collections.Generic;
using System.Linq;
using DotNetty.Buffers;
using DotNetty.Codecs;
using DotNetty.Transport.Channels;
using Nethermind.Logging;
using Nethermind.Network.Rlpx;
using NSubstitute;

namespace Nethermind.Network.Test.Rlpx.TestWrappers
{
    internal class ZeroFrameDecoderTestWrapper : ZeroFrameDecoder
    {
        private readonly IChannelHandlerContext _context;

        public ZeroFrameDecoderTestWrapper(IFrameCipher frameCipher, IFrameMacProcessor frameMacProcessor) : base(frameCipher, frameMacProcessor, LimboLogs.Instance)
        {
            _context = Substitute.For<IChannelHandlerContext>();
            _context.Allocator.Returns(PooledByteBufferAllocator.Default);
        }

        public IByteBuffer Decode(IByteBuffer input, bool throwOnCorruptedFrames = true)
        {
            List<object> result = new List<object>();
            try
            {
                base.Decode(_context, input, result);
            }
            catch (CorruptedFrameException)
            {
                if (throwOnCorruptedFrames)
                {
                    throw;
                }
            }

            if (result.Any())
            {
                return (IByteBuffer) result[0];
            }

            return null;
        }
    }
}
