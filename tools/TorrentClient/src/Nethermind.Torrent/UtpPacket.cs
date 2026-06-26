// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;

namespace Nethermind.Torrent;

internal enum UtpPacketType : byte
{
    Data = 0,
    Fin = 1,
    State = 2,
    Reset = 3,
    Syn = 4,
}

internal sealed class UtpPacketHeader : IEquatable<UtpPacketHeader>
{
    public const byte CurrentVersion = 1;
    public const int BaseHeaderLength = 20;

    public UtpPacketType PacketType { get; init; }

    public byte Version { get; init; } = CurrentVersion;

    public ushort ConnectionId { get; init; }

    public uint TimestampMicros { get; init; }

    public uint TimestampDeltaMicros { get; init; }

    public uint WindowSize { get; init; }

    public ushort SequenceNumber { get; init; }

    public ushort AckNumber { get; init; }

    public byte[]? SelectiveAck { get; init; }

    public static bool TryDecode(ReadOnlySpan<byte> packet, out UtpPacketHeader? header, out int headerLength)
    {
        header = null;
        headerLength = 0;
        if (packet.Length < BaseHeaderLength)
        {
            return false;
        }

        byte typeAndVersion = packet[0];
        UtpPacketType packetType = (UtpPacketType)(typeAndVersion >> 4);
        byte version = (byte)(typeAndVersion & 0x0f);
        if (!IsValidPacketType(packetType) || version != CurrentVersion)
        {
            return false;
        }

        byte nextExtension = packet[1];
        ushort connectionId = BinaryPrimitives.ReadUInt16BigEndian(packet[2..4]);
        uint timestampMicros = BinaryPrimitives.ReadUInt32BigEndian(packet[4..8]);
        uint timestampDeltaMicros = BinaryPrimitives.ReadUInt32BigEndian(packet[8..12]);
        uint windowSize = BinaryPrimitives.ReadUInt32BigEndian(packet[12..16]);
        ushort sequenceNumber = BinaryPrimitives.ReadUInt16BigEndian(packet[16..18]);
        ushort ackNumber = BinaryPrimitives.ReadUInt16BigEndian(packet[18..20]);
        int read = BaseHeaderLength;
        byte[]? selectiveAck = null;

        while (nextExtension != 0)
        {
            if (packet.Length - read < 2)
            {
                return false;
            }

            byte extensionType = nextExtension;
            nextExtension = packet[read];
            byte extensionLength = packet[read + 1];
            read += 2;
            if (packet.Length - read < extensionLength)
            {
                return false;
            }

            if (extensionType == 1)
            {
                if (!IsValidSelectiveAckLength(extensionLength))
                {
                    return false;
                }

                selectiveAck = packet.Slice(read, extensionLength).ToArray();
            }

            read += extensionLength;
        }

        header = new UtpPacketHeader
        {
            PacketType = packetType,
            Version = version,
            ConnectionId = connectionId,
            TimestampMicros = timestampMicros,
            TimestampDeltaMicros = timestampDeltaMicros,
            WindowSize = windowSize,
            SequenceNumber = sequenceNumber,
            AckNumber = ackNumber,
            SelectiveAck = selectiveAck,
        };
        headerLength = read;
        return true;
    }

    public int GetEncodedLength() => BaseHeaderLength + (SelectiveAck is null ? 0 : 2 + SelectiveAck.Length);

    public int Encode(Span<byte> destination)
    {
        int length = GetEncodedLength();
        if (!IsValidPacketType(PacketType) || Version != CurrentVersion)
        {
            throw new InvalidOperationException("uTP packet type or version is not supported.");
        }

        if (SelectiveAck is not null && !IsValidSelectiveAckLength(SelectiveAck.Length))
        {
            throw new InvalidOperationException("uTP selective ACK extension length must be at least 4 bytes and a multiple of 4.");
        }

        if (destination.Length < length)
        {
            throw new ArgumentException("Destination buffer is too small.", nameof(destination));
        }

        destination[0] = (byte)(((byte)PacketType << 4) | (Version & 0x0f));
        destination[1] = SelectiveAck is null ? (byte)0 : (byte)1;
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..4], ConnectionId);
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..8], TimestampMicros);
        BinaryPrimitives.WriteUInt32BigEndian(destination[8..12], TimestampDeltaMicros);
        BinaryPrimitives.WriteUInt32BigEndian(destination[12..16], WindowSize);
        BinaryPrimitives.WriteUInt16BigEndian(destination[16..18], SequenceNumber);
        BinaryPrimitives.WriteUInt16BigEndian(destination[18..20], AckNumber);

        int written = BaseHeaderLength;
        if (SelectiveAck is not null)
        {
            destination[written++] = 0;
            destination[written++] = checked((byte)SelectiveAck.Length);
            SelectiveAck.CopyTo(destination[written..]);
            written += SelectiveAck.Length;
        }

        return written;
    }

    public bool Equals(UtpPacketHeader? other)
    {
        if (other is null)
        {
            return false;
        }

        return PacketType == other.PacketType &&
            Version == other.Version &&
            ConnectionId == other.ConnectionId &&
            TimestampMicros == other.TimestampMicros &&
            TimestampDeltaMicros == other.TimestampDeltaMicros &&
            WindowSize == other.WindowSize &&
            SequenceNumber == other.SequenceNumber &&
            AckNumber == other.AckNumber &&
            EqualBytes(SelectiveAck, other.SelectiveAck);
    }

    public override bool Equals(object? obj) => obj is UtpPacketHeader other && Equals(other);

    public override int GetHashCode()
    {
        HashCode hashCode = new();
        hashCode.Add(PacketType);
        hashCode.Add(Version);
        hashCode.Add(ConnectionId);
        hashCode.Add(TimestampMicros);
        hashCode.Add(TimestampDeltaMicros);
        hashCode.Add(WindowSize);
        hashCode.Add(SequenceNumber);
        hashCode.Add(AckNumber);
        if (SelectiveAck is not null)
        {
            for (int i = 0; i < SelectiveAck.Length; i++)
            {
                hashCode.Add(SelectiveAck[i]);
            }
        }

        return hashCode.ToHashCode();
    }

    private static bool EqualBytes(byte[]? left, byte[]? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return left.AsSpan().SequenceEqual(right);
    }

    private static bool IsValidSelectiveAckLength(int length) => length >= 4 && length % 4 == 0;

    private static bool IsValidPacketType(UtpPacketType packetType) => packetType is >= UtpPacketType.Data and <= UtpPacketType.Syn;
}

internal sealed class UtpRollingDelay
{
    private readonly int[] _values;
    private int _next;
    private int _count;
    private long _sum;

    public UtpRollingDelay(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _values = new int[capacity];
    }

    public void Observe(int value)
    {
        if (_count == _values.Length)
        {
            _sum -= _values[_next];
        }
        else
        {
            _count++;
        }

        _values[_next] = value;
        _sum += value;
        _next = (_next + 1) % _values.Length;
    }

    public int Average => _count == 0 ? 0 : (int)(_sum / _count);
}
