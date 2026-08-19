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

        public ZeroFrameMergerTestWrapper()
            : base(LimboLogs.Instance)
        {
            IByteBufferAllocator allocator = Substitute.For<IByteBufferAllocator>();
            allocator.Buffer(Arg.Any<int>()).Returns(call => AllocatedBuffer = UnpooledByteBufferAllocator.Default.Buffer(call.Arg<int>()));
            _context.Allocator.Returns(allocator);
        }

        /// <summary>The buffer most recently allocated for an in-progress chunked packet, or <c>null</c> if there was none.</summary>
        public IByteBuffer AllocatedBuffer { get; private set; }

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
