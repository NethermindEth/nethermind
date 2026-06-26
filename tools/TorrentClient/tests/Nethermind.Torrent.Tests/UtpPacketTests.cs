// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;

namespace Nethermind.Torrent.Tests;

[TestFixture]
public sealed class UtpPacketTests
{
    [Test]
    public void Header_roundtrips_with_selective_ack_extension()
    {
        UtpPacketHeader expected = new()
        {
            PacketType = UtpPacketType.Syn,
            Version = UtpPacketHeader.CurrentVersion,
            ConnectionId = 123,
            TimestampMicros = 456,
            TimestampDeltaMicros = 789,
            WindowSize = 1024,
            SequenceNumber = 11,
            AckNumber = 9,
            SelectiveAck = [0b1010_0001, 0, 0, 0],
        };
        byte[] buffer = new byte[expected.GetEncodedLength()];

        int written = expected.Encode(buffer);
        bool decoded = UtpPacketHeader.TryDecode(buffer, out UtpPacketHeader? actual, out int headerLength);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(written, Is.EqualTo(buffer.Length));
            Assert.That(decoded, Is.True);
            Assert.That(headerLength, Is.EqualTo(buffer.Length));
            Assert.That(actual, Is.EqualTo(expected));
        }
    }

    [Test]
    public void Header_rejects_invalid_selective_ack_length()
    {
        UtpPacketHeader header = new()
        {
            PacketType = UtpPacketType.State,
            ConnectionId = 1,
            SelectiveAck = [1],
        };
        byte[] packet = new byte[header.GetEncodedLength()];

        Assert.That(() => header.Encode(packet), Throws.TypeOf<InvalidOperationException>());
    }

    [TestCase(0x05)]
    [TestCase(0xf1)]
    public void Header_decode_rejects_unsupported_type_or_version(byte firstByte)
    {
        byte[] packet = new byte[UtpPacketHeader.BaseHeaderLength];
        packet[0] = firstByte;

        bool decoded = UtpPacketHeader.TryDecode(packet, out UtpPacketHeader? header, out int headerLength);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded, Is.False);
            Assert.That(header, Is.Null);
            Assert.That(headerLength, Is.EqualTo(0));
        }
    }
}
