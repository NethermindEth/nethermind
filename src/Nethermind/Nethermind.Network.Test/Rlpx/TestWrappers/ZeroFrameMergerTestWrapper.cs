// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using DotNetty.Buffers;
using DotNetty.Transport.Channels;
using Nethermind.Logging;
using Nethermind.Network.Rlpx;
using NSubstitute;

namespace Nethermind.Network.Test.Rlpx.TestWrappers
{
    internal class ZeroFrameMergerTestWrapper : ZeroFrameMerger
    {
        public ZeroFrameMergerTestWrapper()
            : base(LimboLogs.Instance)
        {
            _context.Allocator.Returns(UnpooledByteBufferAllocator.Default);
        }

        private readonly IChannelHandlerContext _context = Substitute.For<IChannelHandlerContext>();

        public ZeroPacket Decode(IByteBuffer input)
        {
            List<object> result = new();
            while (input.IsReadable())
            {
                base.Decode(_context, input, result);
            }

            if (!result.Any())
            {
                return null;
            }

            return (ZeroPacket)result[0];
        }
    }
}
