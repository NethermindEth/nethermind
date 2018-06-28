/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using DotNetty.Buffers;
using DotNetty.Transport.Channels;
using Nethermind.Core;
using Nethermind.Core.Logging;
using Nethermind.Network.Rlpx;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.Rlpx
{
    [TestFixture]
    public class NettyFrameEncoderTests
    {
        [SetUp]
        public void Setup()
        {
            _frameCipher = Substitute.For<IFrameCipher>();
            _macProcessor = Substitute.For<IFrameMacProcessor>();

            _frame = new byte[16 + 16 + 16 + 16];
            _frame[2] = 16; // size   
        }

        private byte[] _frame;
        private IFrameCipher _frameCipher;
        private IFrameMacProcessor _macProcessor;

        private class UnderTest : NettyFrameEncoder
        {
            private readonly IChannelHandlerContext _context;

            public UnderTest(IFrameCipher frameCipher, IFrameMacProcessor frameMacProcessor) : base(frameCipher, frameMacProcessor, NullLogger.Instance)
            {
                _context = Substitute.For<IChannelHandlerContext>();
            }

            public void Encode(byte[] message, IByteBuffer buffer)
            {
                base.Encode(_context, message, buffer);
            }
        }

        [Test]
        public void Encrypts_header_then_calculates_header_mac()
        {
            IByteBuffer result = Unpooled.Buffer(256);
            UnderTest underTest = new UnderTest(_frameCipher, _macProcessor);
            underTest.Encode(_frame, result);

            Received.InOrder(
                () =>
                {
                    _frameCipher.Received().Encrypt(_frame, 0, 16, _frame, 0);
                    _macProcessor.Received().AddMac(_frame, 0, 16, true);
                }
            );
        }

        [Test]
        public void Encrypts_payload_then_calculates_frame_mac()
        {
            IByteBuffer result = Unpooled.Buffer(256);
            UnderTest underTest = new UnderTest(_frameCipher, _macProcessor);
            underTest.Encode(_frame, result);

            Received.InOrder(
                () =>
                {
                    _frameCipher.Received().Encrypt(_frame, 32, 16, _frame, 32);
                    _macProcessor.Received().AddMac(_frame, 32, 16, false);
                }
            );
        }
    }
}