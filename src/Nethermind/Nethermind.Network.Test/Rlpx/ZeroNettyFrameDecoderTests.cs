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
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Test.Rlpx.Handshake;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.Rlpx
{
    [TestFixture]
    public class ZeroNettyFrameDecoderTests
    {
        private const int LongFrameSize = 48;
        private const int ShortFrameSize = 32;

        [SetUp]
        public void Setup()
        {
            var secrets = NetTestVectors.GetSecretsPair();

            _frameCipher = new FrameCipher(secrets.A.AesSecret);
            _macProcessor = new FrameMacProcessor(TestItem.IgnoredPublicKey, secrets.A);

            _frame = new byte[16 + 16 + LongFrameSize + 16]; //  header | header MAC | packet type | data | padding | frame MAC
            _frame[2] = LongFrameSize - 15; // size (total - padding)

            _shortFrame = new byte[16 + 16 + 1 + ShortFrameSize + 15 + 16]; //  header | header MAC | packet type | data | padding | frame MAC
            _shortFrame[2] = ShortFrameSize - 15; // size (total - padding)
        }

        private byte[] _frame;
        private byte[] _shortFrame;
        private IFrameCipher _frameCipher;
        private IFrameMacProcessor _macProcessor;

        private class UnderTest : ZeroNettyFrameDecoder
        {
            private readonly IChannelHandlerContext _context;

            public UnderTest(IFrameCipher frameCipher, IFrameMacProcessor frameMacProcessor) : base(frameCipher, frameMacProcessor, LimboLogs.Instance)
            {
                _context = Substitute.For<IChannelHandlerContext>();
            }

            public IByteBuffer Decode(IByteBuffer input)
            {
                List<object> result = new List<object>();
                base.Decode(_context, input, result);
                return (IByteBuffer)result[0];
            }
        }

        [Test]
        public void Check_and_decrypt_block()
        {
            byte[] frame = Bytes.FromHexString("96cf8b950a261eae89f0e0cd0432c7d1363cbe6becec70d9fc2a0a2a6e22b2277a4acc5efc408b693073fe8bfb82068dfcf279d80dafce41dbc4f658d92add3bb276063415c4dbacf81bbd2b0a1254eb858522b77417c9e3d6d36d67454c6c45188c642657ffdd5a67c0e2dabd5db24cd8702662f6d041ff896dcf1ef958fa37ef49187302c9ec43ea5cf3828119e84658d397b4646316636dbe4295c5e5b2df69e72c75b32fc03a1e0ec227d3b94fcd4e1f5b593e3dca74d0d327cc2a31402e57f2e62d3b721a8131d40a35e7c2d1babfe3578814f51444b518917e940721eebeabac4b70ad82c21e5270c7434907a92543914698a0cc6c692a33ad6fafc591be2de18e6c297d07a5992cc68adb27cefd2d035b729ba707432f328b43abbd52");
            IByteBuffer input = Unpooled.Buffer(256);
            UnderTest underTest = new UnderTest(_frameCipher, _macProcessor);

            input.WriteBytes(frame);
            IByteBuffer result = underTest.Decode(input);

            byte[] resultBytes = new byte[result.ReadableBytes];
            result.ReadBytes(resultBytes);
            TestContext.WriteLine(resultBytes.ToHexString());
            Assert.AreEqual("0000e1c180000000000000000000000081c604f04ef90243f9023ff901f9a0ff483e972a04a9a62bb4b7d04ae403c615604e4090521ecc5bb7af67f71be09ca01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d493479400000042020080a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421ee2100092104b901427600fe0100fe0100fe0100be010024830f424080833d090080010a9883010203a02ba5557a4c62a513c7e56d1bf13373e0da6bec016755483e91589fe1c6d212e2880025252403e8f840df800182520852bf011001808080807e200004c080000000000000000000000000000000", resultBytes.ToHexString());
        }
    }
}