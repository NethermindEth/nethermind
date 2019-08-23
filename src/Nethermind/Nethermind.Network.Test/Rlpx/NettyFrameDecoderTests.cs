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

using System.Collections.Generic;
using DotNetty.Buffers;
using DotNetty.Transport.Channels;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Test.Rlpx.Handshake;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.Rlpx
{
    [TestFixture]
    public class NettyFrameDecoderTests
    {
        private const int LongFrameSize = 48;
        private const int ShortFrameSize = 32;

        [SetUp]
        public void Setup()
        {
            _frameCipher = Substitute.For<IFrameCipher>();
            _macProcessor = Substitute.For<IFrameMacProcessor>();

            _frame = new byte[16 + 16 + LongFrameSize + 16]; //  header | header MAC | packet type | data | padding | frame MAC
            _frame[2] = LongFrameSize - 15; // size (total - padding)

            _shortFrame = new byte[16 + 16 + 1 + ShortFrameSize + 15 + 16]; //  header | header MAC | packet type | data | padding | frame MAC
            _shortFrame[2] = ShortFrameSize - 15; // size (total - padding)
        }

        private byte[] _frame;
        private byte[] _shortFrame;
        private IFrameCipher _frameCipher;
        private IFrameMacProcessor _macProcessor;

        private class UnderTest : NettyFrameDecoder
        {
            private readonly IChannelHandlerContext _context;

            public UnderTest(IFrameCipher frameCipher, IFrameMacProcessor frameMacProcessor) : base(frameCipher, frameMacProcessor, LimboLogs.Instance)
            {
                _context = Substitute.For<IChannelHandlerContext>();
            }

            public void Decode(IByteBuffer buffer)
            {
                List<object> result = new List<object>();
                base.Decode(_context, buffer, result);
            }
        }

        [Test]
        public void Checks_header_mac_then_decrypts()
        {
            IByteBuffer buffer = Unpooled.Buffer(256);
            buffer.WriteBytes(_frame);

            UnderTest underTest = new UnderTest(_frameCipher, _macProcessor);
            underTest.Decode(buffer);

            Received.InOrder(
                () =>
                {
                    _macProcessor.Received().CheckMac(Arg.Any<byte[]>(), 0, 16, true);
                    _frameCipher.Received().Decrypt(Arg.Any<byte[]>(), 0, 16, Arg.Any<byte[]>(), 0);
                }
            );
        }

        [Test]
        public void Can_decode_frame_after_frame()
        {
            IByteBuffer buffer = Unpooled.Buffer(256);
            buffer.WriteBytes(_frame);
            buffer.WriteBytes(_shortFrame);

            UnderTest underTest = new UnderTest(_frameCipher, _macProcessor);
            underTest.Decode(buffer);
            underTest.Decode(buffer);

            Received.InOrder(
                () =>
                {
                    _macProcessor.Received().CheckMac(Arg.Any<byte[]>(), 32, LongFrameSize, false);
                    _frameCipher.Received().Decrypt(Arg.Any<byte[]>(), 32, LongFrameSize, Arg.Any<byte[]>(), 32);
                    _macProcessor.Received().CheckMac(Arg.Any<byte[]>(), 32, ShortFrameSize, false);
                    _frameCipher.Received().Decrypt(Arg.Any<byte[]>(), 32, ShortFrameSize, Arg.Any<byte[]>(), 32);
                }
            );
        }

        [Test]
        public void Checks_payload_mac_then_decrypts_header_payload()
        {
            IByteBuffer buffer = Unpooled.Buffer(256);
            buffer.WriteBytes(_frame);

            UnderTest underTest = new UnderTest(_frameCipher, _macProcessor);
            underTest.Decode(buffer);

            Received.InOrder(
                () =>
                {
                    _macProcessor.Received().CheckMac(Arg.Any<byte[]>(), 32, LongFrameSize, false);
                    _frameCipher.Received().Decrypt(Arg.Any<byte[]>(), 32, LongFrameSize, Arg.Any<byte[]>(), 32);
                }
            );
        }
    }
}