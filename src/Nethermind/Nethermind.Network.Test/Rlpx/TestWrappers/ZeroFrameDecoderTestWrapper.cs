// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

        public ZeroFrameDecoderTestWrapper(IFrameCipher frameCipher, FrameMacProcessor frameMacProcessor) : base(frameCipher, frameMacProcessor, LimboLogs.Instance)
        {
            _context = Substitute.For<IChannelHandlerContext>();
            _context.Allocator.Returns(PooledByteBufferAllocator.Default);
        }

        public IByteBuffer Decode(IByteBuffer input, bool throwOnCorruptedFrames = true)
        {
            List<object> result = new();
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
                return (IByteBuffer)result[0];
            }

            return null;
        }
    }
}
