// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using DotNetty.Transport.Channels;
using Nethermind.Network.Rlpx;
using NSubstitute;

namespace Nethermind.Network.Test.Rlpx.TestWrappers
{
    internal class ZeroFrameEncoderTestWrapper(IFrameCipher frameCipher, IFrameMacProcessor frameMacProcessor) : ZeroFrameEncoder(frameCipher, frameMacProcessor)
    {
        private readonly IChannelHandlerContext _context = Substitute.For<IChannelHandlerContext>();

        public void Encode(IByteBuffer input, IByteBuffer output) => base.Encode(_context, input, output);
    }
}
