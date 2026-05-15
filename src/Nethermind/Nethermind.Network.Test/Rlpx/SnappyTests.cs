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

namespace Nethermind.Network.Test.Rlpx;

public class SnappyTests
{
    private readonly string _uncompressedTestFileName = Path.Combine(TestContext.CurrentContext.WorkDirectory, "Rlpx", "block.rlp");

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
            IByteBuffer result = UnpooledByteBufferAllocator.Default.Buffer();
            Encode(null, input.ToUnpooledByteBuffer(), result);
            return result.ReadAllBytesAsArray();
        }

        public void TestEncode(IByteBuffer input, IByteBuffer output) => Encode(null, input, output);
    }

    [TestCase("block.go.snappy")]
    [TestCase("block.py.snappy")]
    public void Can_decompress_compressed_file(string compressedFileName)
    {
        SnappyDecoderForTest decoder = new();
        byte[] expectedUncompressed = Bytes.FromHexString(File.ReadAllText(Path.Combine(TestContext.CurrentContext.WorkDirectory, "Rlpx", _uncompressedTestFileName)));
        byte[] compressed = Bytes.FromHexString(File.ReadAllText(Path.Combine(TestContext.CurrentContext.WorkDirectory, "Rlpx", compressedFileName)));
        byte[] uncompressedResult = decoder.TestDecode(compressed);
        Assert.That(uncompressedResult, Is.EqualTo(expectedUncompressed));
    }

    [Test]
    public void Can_load_block_rlp_test_file()
    {
        byte[] bytes = File.ReadAllBytes(_uncompressedTestFileName);
        Assert.That(bytes.Length, Is.GreaterThan(2.9 * 1024 * 1024));
    }

    [TestCase("block.go.snappy")]
    [TestCase("block.py.snappy")]
    public void Can_load_compressed_test_file(string compressedFileName)
    {
        byte[] bytes = Bytes.FromHexString(File.ReadAllText(Path.Combine(TestContext.CurrentContext.WorkDirectory, "Rlpx", compressedFileName)));
        Assert.That(bytes.Length, Is.GreaterThan(70 * 1024));
    }

    [Test]
    [Ignore("Needs further investigation. For now ignoring as it would be requiring too much time.")]
    public void Uses_same_compression_as_py_zero_or_go()
    {
        string rlpxDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "Rlpx");
        byte[] bytesPy = Bytes.FromHexString(File.ReadAllText(Path.Combine(rlpxDir, "block.py.snappy")));
        byte[] bytesGo = Bytes.FromHexString(File.ReadAllText(Path.Combine(rlpxDir, "block.go.snappy")));
        byte[] bytesUncompressed = Bytes.FromHexString(File.ReadAllText(Path.Combine(rlpxDir, _uncompressedTestFileName)));

        ZeroSnappyEncoderForTest encoder = new();
        byte[] compressed = encoder.TestEncode(Bytes.Concat(1, bytesUncompressed));
        bool oneOfTwoMatches = Bytes.AreEqual(bytesGo, compressed) || Bytes.AreEqual(bytesPy, compressed);
        Assert.That(oneOfTwoMatches, Is.True);
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

    /// <summary>
    /// Verifies that Encode does not leak an intermediate pooled IByteBuffer.
    /// Before the fix, ReadBytes(n) allocated a new pooled buffer that was written
    /// to output but never released, leaking one buffer per outbound P2P message.
    /// </summary>
    [Test]
    public void Encode_does_not_leak_pooled_buffers()
    {
        using PooledBufferLeakDetector detector = new();
        ZeroSnappyEncoderForTest encoder = new();

        // RLP-encoded packet type (0x01) followed by an RLP-encoded body
        byte[] packetType = Rlp.Encode(1).Bytes;
        byte[] body = Rlp.Encode(new byte[100]).Bytes;
        byte[] payload = Bytes.Concat(packetType, body);
        using DisposableByteBuffer input = detector.Allocator.Buffer().AsDisposable();
        using DisposableByteBuffer output = detector.Allocator.Buffer().AsDisposable();

        input.WriteBytes(payload);

        encoder.TestEncode(input, output);
    }
}
