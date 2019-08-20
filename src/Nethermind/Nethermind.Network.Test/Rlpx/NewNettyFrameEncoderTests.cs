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
    public class NewNettyFrameEncoderTests
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

        private class UnderTest : NewNettyFrameEncoder
        {
            private readonly IChannelHandlerContext _context;

            public UnderTest(IFrameCipher frameCipher, IFrameMacProcessor frameMacProcessor) : base(frameCipher, frameMacProcessor, NullLogger.Instance)
            {
                _context = Substitute.For<IChannelHandlerContext>();
            }

            public void Encode(IByteBuffer input, IByteBuffer output)
            {
                base.Encode(_context, input, output);
            }
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