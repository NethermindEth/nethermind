// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO;
using System.Linq;
using DotNetty.Buffers;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Network.Rlpx;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Network.Test.Rlpx
{
    [TestFixture]
    public class SnappyTests
    {
        private readonly string _uncompressedTestFileName = Path.Combine(TestContext.CurrentContext.WorkDirectory, "Rlpx", "block.rlp");
        private readonly string _goCompressedTestFileName = Path.Combine(TestContext.CurrentContext.WorkDirectory, "Rlpx", "block.go.snappy");
        private readonly string _pythonCompressedTestFileName = Path.Combine(TestContext.CurrentContext.WorkDirectory, "Rlpx", "block.py.snappy");

        public class SnappyDecoderForTest : SnappyDecoder
        {
            public SnappyDecoderForTest()
                : base(LimboTraceLogger.Instance)
            {
            }

            public byte[] TestDecode(byte[] input)
            {
                List<object> result = new();
                Decode(null, new Packet(input), result);
                return ((Packet)result[0]).Data;
            }
        }

        public class ZeroSnappyEncoderForTest : ZeroSnappyEncoder
        {
            public ZeroSnappyEncoderForTest()
                : base(LimboLogs.Instance)
            {
            }

            public byte[] TestEncode(byte[] input)
            {
                var result = UnpooledByteBufferAllocator.Default.Buffer();
                Encode(null, input.ToUnpooledByteBuffer(), result);
                return result.ReadAllBytesAsArray();
            }
        }

        [Test]
        public void Can_decompress_go_compressed_file()
        {
            SnappyDecoderForTest decoder = new();
            byte[] expectedUncompressed = Bytes.FromHexString(File.ReadAllText(Path.Combine(TestContext.CurrentContext.WorkDirectory, "Rlpx", _uncompressedTestFileName)));
            byte[] compressed = Bytes.FromHexString(File.ReadAllText(Path.Combine(TestContext.CurrentContext.WorkDirectory, "Rlpx", _goCompressedTestFileName)));
            byte[] uncompressedResult = decoder.TestDecode(compressed);
            Assert.That(uncompressedResult, Is.EqualTo(expectedUncompressed));
        }

        [Test]
        public void Can_decompress_python_compressed_file()
        {
            SnappyDecoderForTest decoder = new();
            byte[] expectedUncompressed = Bytes.FromHexString(File.ReadAllText(Path.Combine(TestContext.CurrentContext.WorkDirectory, "Rlpx", _uncompressedTestFileName)));
            byte[] compressed = Bytes.FromHexString(File.ReadAllText(Path.Combine(TestContext.CurrentContext.WorkDirectory, "Rlpx", _pythonCompressedTestFileName)));
            byte[] uncompressedResult = decoder.TestDecode(compressed);
            Assert.That(uncompressedResult, Is.EqualTo(expectedUncompressed));
        }

        [Test]
        public void Can_load_block_rlp_test_file()
        {
            byte[] bytes = File.ReadAllBytes(_uncompressedTestFileName);
            Assert.Greater(bytes.Length, 2.9 * 1024 * 1024);
        }

        [Test]
        public void Can_load_go_compressed_test_file()
        {
            byte[] bytes = Bytes.FromHexString(File.ReadAllText(Path.Combine(TestContext.CurrentContext.WorkDirectory, "Rlpx", _goCompressedTestFileName)));
            Assert.Greater(bytes.Length, 70 * 1024);
        }

        [Test]
        public void Can_load_python_compressed_test_file()
        {
            byte[] bytes = Bytes.FromHexString(File.ReadAllText(Path.Combine(TestContext.CurrentContext.WorkDirectory, "Rlpx", _pythonCompressedTestFileName)));
            Assert.Greater(bytes.Length, 70 * 1024);
        }

        [Test]
        [Ignore("Needs further investigation. For now ignoring as it would be requiring too much time.")]
        public void Uses_same_compression_as_py_zero_or_go()
        {
            byte[] bytesPy = Bytes.FromHexString(File.ReadAllText(Path.Combine(TestContext.CurrentContext.WorkDirectory, "Rlpx", _pythonCompressedTestFileName)));
            byte[] bytesGo = Bytes.FromHexString(File.ReadAllText(Path.Combine(TestContext.CurrentContext.WorkDirectory, "Rlpx", _pythonCompressedTestFileName)));
            byte[] bytesUncompressed = Bytes.FromHexString(File.ReadAllText(Path.Combine(TestContext.CurrentContext.WorkDirectory, "Rlpx", _uncompressedTestFileName)));

            ZeroSnappyEncoderForTest encoder = new();
            byte[] compressed = encoder.TestEncode(Bytes.Concat(1, bytesUncompressed));
            bool oneOfTwoMatches = Bytes.AreEqual(bytesGo, compressed) || Bytes.AreEqual(bytesPy, compressed);
            Assert.True(oneOfTwoMatches);
        }

        [Test]
        public void Roundtrip_zero()
        {
            SnappyDecoderForTest decoder = new();
            ZeroSnappyEncoderForTest encoder = new();
            byte[] expectedUncompressed = Bytes.FromHexString(File.ReadAllText(Path.Combine(TestContext.CurrentContext.WorkDirectory, "Rlpx", _uncompressedTestFileName)));
            byte[] compressed = encoder.TestEncode(Bytes.Concat(1, expectedUncompressed));
            byte[] uncompressedResult = decoder.TestDecode(compressed.Skip(1).ToArray());
            Assert.That(uncompressedResult, Is.EqualTo(expectedUncompressed));
        }
    }
}
