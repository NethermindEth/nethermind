// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using DotNetty.Transport.Channels;
using Nethermind.Logging;
using Nethermind.Network.Rlpx;
using NSubstitute;

namespace Nethermind.Network.Test.Rlpx.TestWrappers
{
    internal class ZeroFrameEncoderTestWrapper : ZeroFrameEncoder
    {
        private readonly IChannelHandlerContext _context;

        public ZeroFrameEncoderTestWrapper(IFrameCipher frameCipher, IFrameMacProcessor frameMacProcessor) : base(frameCipher, frameMacProcessor, LimboLogs.Instance)
        {
            _context = Substitute.For<IChannelHandlerContext>();
        }

        public void Encode(IByteBuffer input, IByteBuffer output)
        {
            base.Encode(_context, input, output);
        }
    }
}
