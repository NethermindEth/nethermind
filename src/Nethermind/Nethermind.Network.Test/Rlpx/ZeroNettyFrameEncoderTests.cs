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
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Network.Rlpx;
using NSubstitute;
using NUnit.Framework;
using Org.BouncyCastle.Crypto.Digests;

namespace Nethermind.Network.Test.Rlpx
{
    [TestFixture]
    public class ZeroNettyFrameEncoderTests
    {
        [SetUp]
        public void Setup()
        {
            PublicKey publicKey = new PublicKey(
                "000102030405060708090A0B0C0D0E0F" +
                "101112131415161718191A1B1C1D1E1F" +
                "202122232425262728292A2B2C2D2E2F" +
                "303132333435363738393A3B3C3D3E3F");
            EncryptionSecrets secrets = new EncryptionSecrets();
            secrets.AesSecret = Keccak.EmptyTreeHash.Bytes;
            secrets.MacSecret = Keccak.OfAnEmptySequenceRlp.Bytes;
            secrets.Token = Keccak.OfAnEmptyString.Bytes;
            secrets.EgressMac = new KeccakDigest(256);
            secrets.IngressMac = new KeccakDigest(256);

            _frameCipher = new FrameCipher(secrets.AesSecret);
            _macProcessor = new FrameMacProcessor(publicKey, secrets);

            _frame = new byte[16 + 16 + 16 + 16];
            _frame[2] = 16; // size   
        }

        private byte[] _frame;
        private IFrameCipher _frameCipher;
        private IFrameMacProcessor _macProcessor;

        private class UnderTest : ZeroNettyFrameEncoder
        {
            private readonly IChannelHandlerContext _context;

            public UnderTest(IFrameCipher frameCipher, IFrameMacProcessor frameMacProcessor) : base(frameCipher, frameMacProcessor, LimboLogs.Instance)
            {
                _context = Substitute.For<IChannelHandlerContext>();
            }

            public void Encode(IByteBuffer input, IByteBuffer output)
            {
                base.Encode(_context, input, output);
            }
        }

        [Test]
        public void Encrypt_and_mac_block()
        {
            byte[] frame = Bytes.FromHexString("0000e1c18000000000000000000000000000000000000000000000000000000081c604f04ef90243f9023ff901f9a0ff483e972a04a9a62bb4b7d04ae403c615604e4090521ecc5bb7af67f71be09ca01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d493479400000042020080a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421ee2100092104b901427600fe0100fe0100fe0100be010024830f424080833d090080010a9883010203a02ba5557a4c62a513c7e56d1bf13373e0da6bec016755483e91589fe1c6d212e2880025252403e8f840df800182520852bf011001808080807e200004c08000000000000000000000000000000000000000000000000000000000000000");
            IByteBuffer result = Unpooled.Buffer(256);
            IByteBuffer input = Unpooled.Buffer(256);
            UnderTest underTest = new UnderTest(_frameCipher, _macProcessor);

            input.WriteBytes(frame);
            underTest.Encode(input, result);

            byte[] resultBytes = new byte[result.ReadableBytes];
            result.ReadBytes(resultBytes);
            TestContext.WriteLine(resultBytes.ToHexString());
            Assert.AreEqual("e13025bd4ae2d72b35e6f05a3b2f3aacf9ffe78eb851f84dc3264380eac18603ad9d5d7350d1271323fe1a6c5aeea2b9e9d6d25e317ab957d737577b84de62fe4107cafcc795f832b71b71344fa44317ba4e113df762f4fa5dd7150e1a288d62f5d72438d56e3eda3aed9a4ba1be7eadceb782cf8e48a7ff6a521282388c8a88ac293ce26fad579cd1ea2ae80705856da9b9b33b5ef46b64ee3d44d2ecaa8e0d2d932fdf29d1d575e3266bb6524acfc438687a45c492815481698e0e1860c7f854b3918eb6550bd867dbc417c808ef9c746ac6d605b39a26c731476d3c9d5bea8c095b6e212a8f1575f9287ac04191c912891fcea59f91d555c59621cc80f1ef41bf7c941b4816eae18821a15ca39fc85ee480ddbd800dd7f6b55e0aabe780eb", resultBytes.ToHexString());
        }
        
        [Test]
        public void Encrypts_and_adds_mac()
        {
            IByteBuffer result = Unpooled.Buffer(256);
            IByteBuffer input = Unpooled.Buffer(256);
            UnderTest underTest = new UnderTest(_frameCipher, _macProcessor);

            input.WriteBytes(_frame);
            underTest.Encode(input, result);

            byte[] resultBytes = new byte[result.ReadableBytes];
            result.ReadBytes(resultBytes);
            TestContext.WriteLine(resultBytes.ToHexString());
            Assert.AreEqual("e130d47ccae2d72b35e6f05a3b2f3aac6f8306efaab67e93d97a402718dd627a2c5b59831e282550dafc25955b170246fc3d8d4a9bee410b71fb963e8bc2f2ce", resultBytes.ToHexString());
        }
    }
}