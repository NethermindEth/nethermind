using System.Buffers.Binary;

namespace Nethermind.Network.Discovery;

public class UTPPacketHeader : IEquatable<UTPPacketHeader>
{
    public UTPPacketType PacketType;
    public byte Version;
    public ushort ConnectionId;
    public uint TimestampMicros;
    public uint TimestampDeltaMicros;
    public uint WindowSize;
    public ushort SeqNumber;
    public ushort AckNumber;

    // TODO: Can be optimized to a single long.
    public byte[]? SelectiveAck;

    private const int HEADER_LENGTH = 20;
    public static (UTPPacketHeader, int) DecodePacket(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < HEADER_LENGTH)
        {
            throw new Exception($"Need at least {HEADER_LENGTH} bytes for header");
        }

        ReadOnlySpan<byte> headerSection = payload[..HEADER_LENGTH];

        UTPPacketHeader utpPacket = new UTPPacketHeader();
        utpPacket.PacketType = (UTPPacketType)(headerSection[0] >> 4);
        utpPacket.Version = (byte)(headerSection[0] & 0x0f);

        byte nextExtension = headerSection[1];

        utpPacket.ConnectionId = BinaryPrimitives.ReadUInt16BigEndian(headerSection[2..]);
        utpPacket.TimestampMicros = BinaryPrimitives.ReadUInt32BigEndian(headerSection[4..]);
        utpPacket.TimestampDeltaMicros = BinaryPrimitives.ReadUInt32BigEndian(headerSection[8..]);
        utpPacket.WindowSize = BinaryPrimitives.ReadUInt32BigEndian(headerSection[12..]);
        utpPacket.SeqNumber = BinaryPrimitives.ReadUInt16BigEndian(headerSection[16..]);
        utpPacket.AckNumber = BinaryPrimitives.ReadUInt16BigEndian(headerSection[18..]);

        int readByte = 20;
        while (nextExtension != 0)
        {
            byte nextNextExtension = payload[readByte];
            byte extensionLength = payload[readByte + 1];

            if (nextExtension == 1) // Selective ack. The only known extension.
            {
                ReadOnlySpan<byte> theExtension = payload[(readByte + 2)..((readByte + 2 + extensionLength))];
                utpPacket.SelectiveAck = theExtension.ToArray();
            }

            readByte += 2 + extensionLength;
            nextExtension = nextNextExtension;
        }

        return (utpPacket, readByte);
    }

    public static ReadOnlySpan<byte> EncodePacket(UTPPacketHeader utpPacket, ReadOnlySpan<byte> payload, Span<byte> buffer)
    {
        int headerPortion = EncodePacketHeader(utpPacket, buffer);
        if ((buffer.Length - headerPortion) < payload.Length)
        {
            throw new Exception("buffer not large enough");
        }

        payload.CopyTo(buffer[headerPortion..]);
        return buffer[..(headerPortion + payload.Length)];
    }

    // Note: Payload not encoded.
    public static int EncodePacketHeader(UTPPacketHeader utpPacket, Span<byte> buffer)
    {
        // Mostly ChatGPT generated.

        int totalLength = HEADER_LENGTH;

        if (utpPacket.SelectiveAck != null)
        {
            totalLength += 2 + utpPacket.SelectiveAck.Length; // 1 byte for next extension, 1 byte for length, plus the extension length
        }

        if (buffer.Length < totalLength)
        {
            throw new Exception($"Buffer is too small. Required length: {totalLength}");
        }

        buffer[0] = (byte)(((byte)utpPacket.PacketType << 4) | (utpPacket.Version & 0x0f));

        BinaryPrimitives.WriteUInt16BigEndian(buffer[2..4], utpPacket.ConnectionId);
        BinaryPrimitives.WriteUInt32BigEndian(buffer[4..8], utpPacket.TimestampMicros);
        BinaryPrimitives.WriteUInt32BigEndian(buffer[8..12], utpPacket.TimestampDeltaMicros);
        BinaryPrimitives.WriteUInt32BigEndian(buffer[12..16], utpPacket.WindowSize);
        BinaryPrimitives.WriteUInt16BigEndian(buffer[16..18], utpPacket.SeqNumber);
        BinaryPrimitives.WriteUInt16BigEndian(buffer[18..20], utpPacket.AckNumber);

        int writtenBytes = HEADER_LENGTH;

        if (utpPacket.SelectiveAck != null)
        {
            buffer[1] = 1;

            buffer[writtenBytes++] = 0;
            buffer[writtenBytes++] = (byte)utpPacket.SelectiveAck.Length;

            utpPacket.SelectiveAck.CopyTo(buffer[writtenBytes..]);
            writtenBytes += utpPacket.SelectiveAck.Length;
        }
        else
        {
            buffer[1] = 0;
        }

        return writtenBytes;
    }

    public bool Equals(UTPPacketHeader? other)
    {
        if (ReferenceEquals(null, other)) return false;
        if (ReferenceEquals(this, other)) return true;
        return PacketType == other.PacketType && Version == other.Version && ConnectionId == other.ConnectionId && TimestampMicros == other.TimestampMicros && TimestampDeltaMicros == other.TimestampDeltaMicros && WindowSize == other.WindowSize && SeqNumber == other.SeqNumber && AckNumber == other.AckNumber && TwoArrayEquals(SelectiveAck, other.SelectiveAck);
    }

    private bool TwoArrayEquals(byte[]? array1, byte[]? array2)
    {
        if (array1 == null || array2 == null) return array1 == array2;
        return array1.SequenceEqual(array2);
    }

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(null, obj)) return false;
        if (ReferenceEquals(this, obj)) return true;
        if (obj.GetType() != this.GetType()) return false;
        return Equals((UTPPacketHeader)obj);
    }

    public override int GetHashCode()
    {
        var hashCode = new HashCode();
        hashCode.Add((int)PacketType);
        hashCode.Add(Version);
        hashCode.Add(ConnectionId);
        hashCode.Add(TimestampMicros);
        hashCode.Add(TimestampDeltaMicros);
        hashCode.Add(WindowSize);
        hashCode.Add(SeqNumber);
        hashCode.Add(AckNumber);
        hashCode.Add(SelectiveAck);
        return hashCode.ToHashCode();
    }

    public static bool operator ==(UTPPacketHeader? left, UTPPacketHeader? right)
    {
        return Equals(left, right);
    }

    public static bool operator !=(UTPPacketHeader? left, UTPPacketHeader? right)
    {
        return !Equals(left, right);
    }

    public override string ToString()
    {
        if (SelectiveAck != null)
        {
            string bitSetString = string.Concat(SelectiveAck.Select(b => Convert.ToString(b, 2).PadLeft(8, '0')));
            return $"{PacketType} with seq_nr={SeqNumber} and ack_nr={AckNumber} and selective_ack_nr={bitSetString}";
        }
        return $"{PacketType} {Version} {WindowSize} with seq_nr={SeqNumber} and ack_nr={AckNumber}, {TimestampMicros} {TimestampDeltaMicros}";
    }
}
