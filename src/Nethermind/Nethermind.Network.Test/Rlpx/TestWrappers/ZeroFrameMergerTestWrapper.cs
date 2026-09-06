// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using DotNetty.Buffers;
using DotNetty.Transport.Channels;
using Nethermind.Logging;
using Nethermind.Network.Rlpx;
using NSubstitute;

namespace Nethermind.Network.Test.Rlpx.TestWrappers
{
    internal class ZeroFrameMergerTestWrapper : ZeroFrameMerger
    {
        private readonly IChannelHandlerContext _context = Substitute.For<IChannelHandlerContext>();

        /// <param name="allocator">
        /// Allocator the merger draws in-progress packet buffers from. Pass a <see cref="PooledBufferLeakDetector"/>
        /// allocator to assert those buffers are released.
        /// </param>
        public ZeroFrameMergerTestWrapper(IByteBufferAllocator? allocator = null)
            : base(LimboLogs.Instance) => _context.Allocator.Returns(allocator ?? UnpooledByteBufferAllocator.Default);

        public ZeroPacket Decode(IByteBuffer input)
        {
            List<object> result = [];
            while (input.IsReadable())
            {
                base.Decode(_context, input, result);
            }

            if (result.Count == 0)
            {
                return null;
            }

            return (ZeroPacket)result[0];
        }
    }
}
