// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Linq;
using DotNetty.Buffers;
using DotNetty.Transport.Channels;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.Rlpx;
using Nethermind.Serialization.Rlp;
using NSubstitute;
using NUnit.Framework;
using Snappier;

namespace Nethermind.Network.Test.Rlpx;

public class SnappyTests
{
    private readonly string _uncompressedTestFileName = Path.Combine(TestContext.CurrentContext.WorkDirectory, "Rlpx", "block.rlp");

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

    [Test]
    public void Can_load_block_rlp_test_file()
    {
        byte[] bytes = File.ReadAllBytes(_uncompressedTestFileName);
        Assert.That(bytes.Length, Is.GreaterThan(2.9 * MemorySizes.MiB));
    }

    [TestCase("block.go.snappy")]
    [TestCase("block.py.snappy")]
    public void Can_load_compressed_test_file(string compressedFileName)
    {
        byte[] bytes = Bytes.FromHexString(File.ReadAllText(Path.Combine(TestContext.CurrentContext.WorkDirectory, "Rlpx", compressedFileName)));
        Assert.That(bytes.Length, Is.GreaterThan(70 * MemorySizes.KiB));
    }

    [TestCase("block.go.snappy")]
    [TestCase("block.py.snappy")]
    public void Zero_netty_p2p_handler_can_decompress_compressed_file(string compressedFileName)
    {
        const byte packetType = 0x05;
        byte[] expectedUncompressed = Bytes.FromHexString(File.ReadAllText(_uncompressedTestFileName));
        byte[] compressed = Bytes.FromHexString(File.ReadAllText(Path.Combine(TestContext.CurrentContext.WorkDirectory, "Rlpx", compressedFileName)));

        ISession session = Substitute.For<ISession>();
        IChannelHandlerContext context = Substitute.For<IChannelHandlerContext>();
        context.Allocator.Returns(UnpooledByteBufferAllocator.Default);

        ZeroPacket received = null;
        session.When(static s => s.ReceiveMessage(Arg.Any<ZeroPacket>()))
            .Do(call =>
            {
                received = call.Arg<ZeroPacket>();
                received.Retain();
            });

        IByteBuffer content = Unpooled.Buffer(compressed.Length);
        content.WriteBytes(compressed);
        ZeroPacket packet = new(content)
        {
            PacketType = packetType
        };

        ZeroNettyP2PHandler handler = new(session, LimboLogs.Instance);
        handler.EnableSnappy();

        try
        {
            handler.ChannelRead(context, packet);

            Assert.That(received, Is.Not.Null);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(received.PacketType, Is.EqualTo(packetType));
                Assert.That(received.Content.ReadAllBytesAsArray(), Is.EqualTo(expectedUncompressed));
            }
        }
        finally
        {
            received?.Release();
        }
    }

    [Test]
    [Ignore("Needs further investigation. For now ignoring as it would be requiring too much time.")]
    public void Uses_same_compression_as_py_zero_or_go()
    {
        string rlpxDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "Rlpx");
        byte[] bytesPy = Bytes.FromHexString(File.ReadAllText(Path.Combine(rlpxDir, "block.py.snappy")));
        byte[] bytesGo = Bytes.FromHexString(File.ReadAllText(Path.Combine(rlpxDir, "block.go.snappy")));
        byte[] bytesUncompressed = Bytes.FromHexString(File.ReadAllText(_uncompressedTestFileName));

        ZeroSnappyEncoderForTest encoder = new();
        byte[] compressed = encoder.TestEncode(Bytes.Concat(1, bytesUncompressed));
        bool oneOfTwoMatches = Bytes.AreEqual(bytesGo, compressed) || Bytes.AreEqual(bytesPy, compressed);
        Assert.That(oneOfTwoMatches, Is.True);
    }

    [Test]
    public void Roundtrip_zero()
    {
        ZeroSnappyEncoderForTest encoder = new();
        byte[] expectedUncompressed = Bytes.FromHexString(File.ReadAllText(_uncompressedTestFileName));
        byte[] compressed = encoder.TestEncode(Bytes.Concat(1, expectedUncompressed));
        byte[] uncompressedResult = Snappy.DecompressToArray(compressed.Skip(1).ToArray());
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
