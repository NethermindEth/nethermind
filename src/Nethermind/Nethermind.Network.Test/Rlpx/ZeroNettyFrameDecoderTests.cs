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

            _frameCipher = new FrameCipher(secrets.B.AesSecret);
            _macProcessor = new FrameMacProcessor(TestItem.IgnoredPublicKey, secrets.B);

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
            byte[] frame = Bytes.FromHexString("96cd2d950a261eae89f0e0cd0432c7d142d19e629f6b6d653a1424b0b7dcd1a6fc75caed4bbbb6d3c888618db2453158b06548d9bdb1b8208b70e0075d675bbe80268afff07570005454467c0c3e85a3468e3225015521fecb9a0c1a2092fd445f14552e33d4ebb4d7b533606989210b4c702662b4d2417f293b2701ee4336624916cddefb4c3777d1a144e1df66162b60bcaed3319becbbe19062f7e3715505dc84fa23a531d7dfd35b9ad95042a85faa643a40365d52d0b57e67cc428676cc364567a6c8cf7fda48bd4c54fd2c8fad1e504f3c2451f538793c022fcdfaef9d8fdafae2c989a7e61dba88879cc9062b774bc3f999b0cdece9aab3d34fafc1513e2de18e6c297d07a5992cc68adb27ce3de39c290884e2f6f0f8b24645c622b2cd9d396498e689a44dc775c22f4baa6b5c1685c19b020110c5788b0e61ebf1794f05df9237d61ca95f49f00fa0217c6a0935f5674c32f8eb7b9c5fe351d429ba0383761d980b53b1187a9a367f80075c020fcc71a75689a4a9e74b8c6137ab267ce6e697008ff5f8c29af53fd4f97018da197f990181f112be738fd0f57bcfc72a78dda731b64aecdd3f83586f1fcfdbdf2c58eff42b38fed19730a1fd0dd21d248d78a396689a508561590543dbe62b44475bec724f2c13678dc877d6cc15099634c10ee4206b339e45c4829c8d81537c3b57fab792929f79a391c29d4f10f6db8318d9f2bffffdafb3038f005ab5e9ce4bf46960f7cf078cebc4287da0330f6a02555851db05eb28dab481313b43dd64eb9a70e8b4460da9955c39e5c24d2d3432189911b6bbdc05c5c6d50fc8e2422a833c3b9ff407ed69cf8fa55248cddddab62951b60c822588ae6b22c67bd9126a5a84cb5723a3079d52972341bcf216");
            IByteBuffer input = Unpooled.Buffer(256);
            UnderTest underTest = new UnderTest(_frameCipher, _macProcessor);

            input.WriteBytes(frame);
            IByteBuffer result = underTest.Decode(input);

            byte[] resultBytes = new byte[result.ReadableBytes];
            result.ReadBytes(resultBytes);
            TestContext.WriteLine(resultBytes.ToHexString());
            string expected = "000247c180000000000000000000000007f90243f9023ff901f9a0ff483e972a04a9a62bb4b7d04ae403c615604e4090521ecc5bb7af67f71be09ca01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347940000000000000000000000000000000000000000a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421b9010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000830f424080833d090080830f424083010203a02ba5557a4c62a513c7e56d1bf13373e0da6bec016755483e91589fe1c6d212e28800000000000003e8f840df80018252089400000000000000000000000000000000000000000180808080df80018252089400000000000000000000000000000000000000000180808080c080000000000000000000";
            TestContext.WriteLine(resultBytes.ToHexString());
            Assert.AreEqual(expected, resultBytes.ToHexString());
            Assert.AreEqual(input.ReaderIndex, input.WriterIndex, "reader index == writer index");
        }
    }
}