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
using System.IO;
using Nethermind.Core;
using Nethermind.Network.Rlpx;
using NUnit.Framework;

namespace Nethermind.Network.Test.Rlpx
{
    [TestFixture]
    public class SnappyDecoderTests
    {
        private readonly string _uncompressedTestFileName = Path.Combine(TestContext.CurrentContext.WorkDirectory, "Rlpx", "block.rlp");
        private readonly string _goCompressedTestFileName = Path.Combine(TestContext.CurrentContext.WorkDirectory, "Rlpx", "block.go.snappy");
        private readonly string _pythonCompressedTestFileName = Path.Combine(TestContext.CurrentContext.WorkDirectory, "Rlpx", "block.py.snappy");

        public class SnappyDecoderForTest : SnappyDecoder
        {
            public byte[] TestDecode(byte[] input)
            {
                List<object> result = new List<object>();
                Decode(null, input, result);
                return (byte[])result[0];
            }
        }

        public class SnappyEncoderForTest : SnappyEncoder
        {
            public byte[] TestEncode(byte[] input)
            {
                List<object> result = new List<object>();
                Encode(null, input, result);
                return (byte[])result[0];
            }
        }

        [Test]
        public void Can_decompress_go_compressed_file()
        {
            SnappyDecoderForTest decoder = new SnappyDecoderForTest();
            byte[] expectedUncompressed = new Hex(File.ReadAllText(Path.Combine(TestContext.CurrentContext.WorkDirectory, "Rlpx", _uncompressedTestFileName)));
            byte[] compressed = new Hex(File.ReadAllText(Path.Combine(TestContext.CurrentContext.WorkDirectory, "Rlpx", _goCompressedTestFileName)));
            byte[] uncompressedResult = decoder.TestDecode(compressed);
            Assert.AreEqual(expectedUncompressed, uncompressedResult);
        }

        [Test]
        public void Can_decompress_python_compressed_file()
        {
            SnappyDecoderForTest decoder = new SnappyDecoderForTest();
            byte[] expectedUncompressed = new Hex(File.ReadAllText(Path.Combine(TestContext.CurrentContext.WorkDirectory, "Rlpx", _uncompressedTestFileName)));
            byte[] compressed = new Hex(File.ReadAllText(Path.Combine(TestContext.CurrentContext.WorkDirectory, "Rlpx", _pythonCompressedTestFileName)));
            byte[] uncompressedResult = decoder.TestDecode(compressed);
            Assert.AreEqual(expectedUncompressed, uncompressedResult);
        }

        [Test]
        public void Can_load_block_rlp_test_file()
        {
            byte[] bytes = new Hex(File.ReadAllBytes(_uncompressedTestFileName));
            Assert.Greater(bytes.Length, 2.9 * 1024 * 1024);
        }

        [Test]
        public void Can_load_go_compressed_test_file()
        {
            byte[] bytes = new Hex(File.ReadAllText(Path.Combine(TestContext.CurrentContext.WorkDirectory, "Rlpx", _goCompressedTestFileName)));
            Assert.Greater(bytes.Length, 70 * 1024);
        }

        [Test]
        public void Can_load_python_compressed_test_file()
        {
            byte[] bytes = new Hex(File.ReadAllText(Path.Combine(TestContext.CurrentContext.WorkDirectory, "Rlpx", _pythonCompressedTestFileName)));
            Assert.Greater(bytes.Length, 70 * 1024);
        }

        [Test]
        public void Roundtrip()
        {
            SnappyDecoderForTest decoder = new SnappyDecoderForTest();
            SnappyEncoderForTest encoder = new SnappyEncoderForTest();
            byte[] expectedUncompressed = new Hex(File.ReadAllText(Path.Combine(TestContext.CurrentContext.WorkDirectory, "Rlpx", _uncompressedTestFileName)));
            byte[] compressed = encoder.TestEncode(expectedUncompressed);
            byte[] uncompressedResult = decoder.TestDecode(compressed);
            Assert.AreEqual(expectedUncompressed, uncompressedResult);
        }
    }
}