// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using DotNetty.Buffers;
using DotNetty.Transport.Channels.Embedded;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Network.Rlpx;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;
using Snappier;

namespace Nethermind.Network.Test.Rlpx;

/// <summary>
/// Pins the bytes the RLPx frame codec puts on the wire, captured before the codec moved off DotNetty buffers, so a change
/// that alters both the encoder and the decoder in the same way cannot go unnoticed.
/// </summary>
[Parallelizable(ParallelScope.All)]
public class RlpxFrameWireTests
{
    // Each packet sent without Snappy takes the next context id, starting at 1, so the last two framed packets carry
    // context ids 127 and 128, where the id's RLP grows from one byte to two. Both span two frames.
    private const int FramedPacketCount = 128;
    private const int ChunkedContentLength = Frame.DefaultMaxFrameSize + 76;

    private const string ChunkedFrameHeaders =
        "000400c5807f82044e0000000000000000004ec2807f00000000000000000000000400c680818082044e00000000000000004ec3808180000000000000000000";

    // Covers only the packets sent without Snappy: Snappy allows more than one valid compressed form of the same content,
    // so a Snappier upgrade could change the compressed bytes without breaking anything.
    private const string EncryptedFramedStreamHash = "0xfac25e79183c24f03aa3975de3cf1967ca590fdf31c9f7105875ebabebc8c709";

    private static readonly (int PacketType, int ContentLength)[] SnappyPackets = [(0x80, 3000), (2, 20), (0x10, 70_000)];

    [Test]
    public void Chunked_frame_headers_match_master()
    {
        EmbeddedChannel channel = new(new ZeroPacketSplitter());
        try
        {
            List<byte[]> packets = EncodeFramedPackets(channel);
            byte[] headers = [.. FrameHeaders(packets[^2]), .. FrameHeaders(packets[^1])];
            Assert.That(headers.ToHexString(), Is.EqualTo(ChunkedFrameHeaders));
        }
        finally
        {
            channel.FinishAndReleaseAll();
        }

        static IEnumerable<byte> FrameHeaders(byte[] encoded) =>
            [.. encoded.AsSpan(0, Frame.HeaderSize).ToArray(), .. encoded.AsSpan(Frame.HeaderSize + Frame.DefaultMaxFrameSize, Frame.HeaderSize).ToArray()];
    }

    [Test]
    public void Snappy_frame_keeps_the_packet_type_uncompressed()
    {
        ZeroPacketSplitter splitter = new();
        splitter.EnableSnappy(LimboLogs.Instance);
        EmbeddedChannel channel = new(splitter);
        (int packetType, int contentLength) = SnappyPackets[0];
        byte[] content = Content(packetType, contentLength);
        try
        {
            byte[] encoded = Encode(channel, packetType, content);
            int payloadLength = encoded[0] << 16 | encoded[1] << 8 | encoded[2];
            ReadOnlySpan<byte> payload = encoded.AsSpan(Frame.HeaderSize, payloadLength);
            using (Assert.EnterMultipleScope())
            {
                // The header body is the RLP list [0, 0]: capability id and context id, with no total packet size.
                Assert.That(encoded.AsSpan(3, Frame.HeaderSize - 3).ToArray().ToHexString(), Is.EqualTo("c28080" + new string('0', 20)));
                Assert.That(encoded, Has.Length.EqualTo(Frame.HeaderSize + payloadLength + Frame.CalculatePadding(payloadLength)));
                Assert.That(encoded.AsSpan(Frame.HeaderSize + payloadLength).ToArray(), Is.All.Zero, "padding");
                Assert.That(payload[..2].ToArray().ToHexString(), Is.EqualTo("8180"), "packet type");
                Assert.That(Snappy.DecompressToArray(payload[2..]), Is.EqualTo(content));
            }
        }
        finally
        {
            channel.FinishAndReleaseAll();
        }
    }

    [Test]
    public void Encrypted_stream_matches_master_and_decodes_to_the_packets_sent()
    {
        (EncryptionSecrets a, EncryptionSecrets b) = NetTestVectors.GetSecretsPair();
        using FrameMacProcessor outboundMac = new(a);
        using FrameMacProcessor inboundMac = new(b);
        ZeroPacketSplitter splitter = new(new FrameCipher(a.AesSecret), outboundMac);
        EmbeddedChannel outbound = new(splitter);
        EmbeddedChannel inbound = new(new ZeroFrameDecoder(new FrameCipher(b.AesSecret), inboundMac), new ZeroFrameMerger(LimboLogs.Instance));
        try
        {
            using MemoryStream stream = new();
            foreach (byte[] encoded in EncodeFramedPackets(outbound))
            {
                stream.Write(encoded);
            }

            Assert.That(Keccak.Compute(stream.ToArray()).ToString(), Is.EqualTo(EncryptedFramedStreamHash));

            splitter.EnableSnappy(LimboLogs.Instance);
            foreach ((int packetType, int contentLength) in SnappyPackets)
            {
                stream.Write(Encode(outbound, packetType, Content(packetType, contentLength)));
            }

            byte[] wire = stream.ToArray();

            inbound.WriteInbound(Unpooled.WrappedBuffer(wire));
            for (int i = 1; i <= FramedPacketCount; i++)
            {
                (int packetType, int contentLength) = FramedPacket(i);
                AssertPacket(inbound.ReadInbound<NettyBufferPacket>(), packetType, Content(packetType, contentLength), snappy: false);
            }

            foreach ((int packetType, int contentLength) in SnappyPackets)
            {
                AssertPacket(inbound.ReadInbound<NettyBufferPacket>(), packetType, Content(packetType, contentLength), snappy: true);
            }

            Assert.That(inbound.ReadInbound<NettyBufferPacket>(), Is.Null);
        }
        finally
        {
            outbound.FinishAndReleaseAll();
            inbound.FinishAndReleaseAll();
        }
    }

    /// <summary>Gives the packet that takes context id <paramref name="contextId"/>: small single-frame packets with varied padding, then two chunked ones, the first with a packet type that takes two RLP bytes.</summary>
    private static (int PacketType, int ContentLength) FramedPacket(int contextId) => contextId switch
    {
        FramedPacketCount - 1 => (0x80, ChunkedContentLength),
        FramedPacketCount => (3, ChunkedContentLength + 1),
        _ => (2 + contextId % 3, 1 + contextId % 40),
    };

    private static List<byte[]> EncodeFramedPackets(EmbeddedChannel channel)
    {
        List<byte[]> packets = [with(FramedPacketCount)];
        for (int i = 1; i <= FramedPacketCount; i++)
        {
            (int packetType, int contentLength) = FramedPacket(i);
            packets.Add(Encode(channel, packetType, Content(packetType, contentLength)));
        }

        return packets;
    }

    private static byte[] Content(int packetType, int length)
    {
        byte[] content = new byte[length];
        for (int i = 0; i < length; i++)
        {
            content[i] = (byte)(i * 31 + packetType + i / 251);
        }

        return content;
    }

    private static byte[] Encode(EmbeddedChannel channel, int packetType, byte[] content)
    {
        IByteBuffer packet = Unpooled.Buffer(2 + content.Length);
        if (packetType >= 0x80) packet.WriteByte(0x81);
        packet.WriteByte(packetType);
        packet.WriteBytes(content);
        channel.WriteOutbound(packet);
        IByteBuffer encoded = channel.ReadOutbound<IByteBuffer>();
        try
        {
            return encoded.ReadAllBytesAsArray();
        }
        finally
        {
            encoded.Release();
        }
    }

    private static void AssertPacket(NettyBufferPacket? packet, int packetType, byte[] content, bool snappy)
    {
        Assert.That(packet, Is.Not.Null);
        try
        {
            byte[] received = packet!.Buffer.ReadAllBytesAsArray();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(packet.PacketType, Is.EqualTo(packetType));
                Assert.That(snappy ? Snappy.DecompressToArray(received) : received, Is.EqualTo(content));
            }
        }
        finally
        {
            packet!.Release();
        }
    }
}
