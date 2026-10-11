// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Nethermind.Network.P2P.Subprotocols.Lean.Ethp2p;

/// <summary>Malformed broadcast framework data.</summary>
internal sealed class LeanBroadcastFormatException(string message) : Exception(message);

/// <summary>A decoded <c>Bcast</c> control frame.</summary>
internal abstract record LeanBcastFrame;

internal sealed record LeanBcastHandshake(uint Version, string[] Channels) : LeanBcastFrame;

internal sealed record LeanBcastSubscribe(string Channel) : LeanBcastFrame;

internal sealed record LeanBcastUnsubscribe(string Channel) : LeanBcastFrame;

/// <summary>A decoded <c>Sess</c> frame.</summary>
internal abstract record LeanSessFrame;

internal sealed record LeanSessOpen(string Channel, string MessageId, byte[] Preamble, byte[] InitialUpdate) : LeanSessFrame;

internal sealed record LeanSessUpdate(byte[] Data) : LeanSessFrame;

internal sealed record LeanChunkHeader(string Channel, string MessageId, byte[] ChunkId, uint DataLength);

/// <summary>
/// Protobuf encodings of the ethp2p broadcast framework (<c>broadcast/pb/broadcast.proto</c>), U32-length-prefixed envelopes,
/// and the EIP-8437 shard identifiers and bitmaps.
/// </summary>
/// <remarks>Decoding follows proto3: unknown fields are skipped and the last value of a repeated scalar wins, while strings
/// must be UTF-8 and nested lengths must stay inside their message.</remarks>
internal static class LeanBroadcastWire
{
    public const uint FrameworkVersion = 1;
    public const int EnvelopeLengthBytes = sizeof(uint);
    public const int BitmapBytes = LeanReedSolomon.TotalShards / 8;
    private const int MaxStringBytes = 256;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] Envelope(ReadOnlySpan<byte> message)
    {
        byte[] envelope = new byte[EnvelopeLengthBytes + message.Length];
        BinaryPrimitives.WriteUInt32BigEndian(envelope, (uint)message.Length);
        message.CopyTo(envelope.AsSpan(EnvelopeLengthBytes));
        return envelope;
    }

    public static byte[] Handshake(params string[] channels)
    {
        List<byte> inner = [];
        WriteVarintField(inner, 1, FrameworkVersion);
        foreach (string channel in channels) WriteBytesField(inner, 2, Encoding.UTF8.GetBytes(channel));
        return Message(1, inner);
    }

    public static byte[] Subscribe(string channel) => Message(2, StringMessage(channel));

    public static byte[] Unsubscribe(string channel) => Message(3, StringMessage(channel));

    public static byte[] SessOpen(string channel, string messageId, ReadOnlySpan<byte> preamble, ReadOnlySpan<byte> initialUpdate)
    {
        List<byte> inner = [];
        WriteBytesField(inner, 1, Encoding.UTF8.GetBytes(channel));
        WriteBytesField(inner, 2, Encoding.UTF8.GetBytes(messageId));
        WriteBytesField(inner, 3, preamble);
        if (!initialUpdate.IsEmpty) WriteBytesField(inner, 4, initialUpdate);
        return Message(1, inner);
    }

    public static byte[] SessUpdate(ReadOnlySpan<byte> data)
    {
        List<byte> inner = [];
        WriteBytesField(inner, 1, data);
        return Message(2, inner);
    }

    public static byte[] ChunkHeader(string channel, string messageId, ReadOnlySpan<byte> chunkId, uint dataLength)
    {
        List<byte> header = [];
        WriteBytesField(header, 1, Encoding.UTF8.GetBytes(channel));
        WriteBytesField(header, 2, Encoding.UTF8.GetBytes(messageId));
        if (!chunkId.IsEmpty) WriteBytesField(header, 3, chunkId);
        if (dataLength != 0) WriteVarintField(header, 4, dataLength);
        return [.. header];
    }

    public static LeanBcastFrame DecodeBcast(ReadOnlySpan<byte> message)
    {
        int field = ReadOneof(message, 3, out ReadOnlySpan<byte> inner);
        switch (field)
        {
            case 1:
                uint version = 0;
                List<string> channels = [];
                for (Reader reader = new(inner); reader.Next(out int number, out int wireType);)
                {
                    if (number == 1 && wireType == 0) version = (uint)reader.Varint();
                    else if (number == 2 && wireType == 2)
                    {
                        if (channels.Count == 2 * LeanProtocol.MaxProfiles) throw new LeanBroadcastFormatException("Too many handshake channels");
                        channels.Add(Utf8(reader.Bytes()));
                    }
                    else reader.Skip(wireType);
                }
                return new LeanBcastHandshake(version, [.. channels]);
            case 2:
                return new LeanBcastSubscribe(ReadStringMessage(inner));
            default:
                return new LeanBcastUnsubscribe(ReadStringMessage(inner));
        }
    }

    public static LeanSessFrame DecodeSess(ReadOnlySpan<byte> message)
    {
        int field = ReadOneof(message, 2, out ReadOnlySpan<byte> inner);
        if (field == 2)
        {
            byte[] data = [];
            for (Reader reader = new(inner); reader.Next(out int number, out int wireType);)
                if (number == 1 && wireType == 2) data = reader.Bytes().ToArray();
                else reader.Skip(wireType);
            return new LeanSessUpdate(data);
        }
        string channel = "", messageId = "";
        byte[] preamble = [], initialUpdate = [];
        for (Reader reader = new(inner); reader.Next(out int number, out int wireType);)
        {
            if (wireType != 2)
            {
                reader.Skip(wireType);
                continue;
            }
            switch (number)
            {
                case 1: channel = Utf8(reader.Bytes()); break;
                case 2: messageId = Utf8(reader.Bytes()); break;
                case 3: preamble = reader.Bytes().ToArray(); break;
                case 4: initialUpdate = reader.Bytes().ToArray(); break;
                default: reader.Bytes(); break;
            }
        }
        return new LeanSessOpen(channel, messageId, preamble, initialUpdate);
    }

    public static LeanChunkHeader DecodeChunkHeader(ReadOnlySpan<byte> message)
    {
        string channel = "", messageId = "";
        byte[] chunkId = [];
        uint dataLength = 0;
        for (Reader reader = new(message); reader.Next(out int number, out int wireType);)
        {
            switch (number, wireType)
            {
                case (1, 2): channel = Utf8(reader.Bytes()); break;
                case (2, 2): messageId = Utf8(reader.Bytes()); break;
                case (3, 2): chunkId = reader.Bytes().ToArray(); break;
                case (4, 0):
                    ulong length = reader.Varint();
                    if (length > uint.MaxValue) throw new LeanBroadcastFormatException("data_length exceeds 32 bits");
                    dataLength = (uint)length;
                    break;
                default: reader.Skip(wireType); break;
            }
        }
        return new LeanChunkHeader(channel, messageId, chunkId, dataLength);
    }

    /// <summary>The canonical <c>ChunkIdent { int32 index = 1; }</c>: empty for index zero, <c>0x08 || U8(index)</c> otherwise.</summary>
    public static byte[] ShardId(int index)
    {
        if ((uint)index >= LeanReedSolomon.TotalShards) throw new ArgumentOutOfRangeException(nameof(index));
        return index == 0 ? [] : [0x08, (byte)index];
    }

    /// <summary>Parses a shard identifier, accepting only its canonical encoding.</summary>
    public static bool TryParseShardId(ReadOnlySpan<byte> encoded, out int index)
    {
        index = 0;
        if (encoded.IsEmpty) return true;
        if (encoded.Length != 2 || encoded[0] != 0x08 || encoded[1] is 0 or >= LeanReedSolomon.TotalShards) return false;
        index = encoded[1];
        return true;
    }

    /// <summary>Four bytes; shard <c>i</c> is bit <c>i % 8</c> of byte <c>i / 8</c>.</summary>
    public static byte[] Bitmap(uint shards)
    {
        byte[] bitmap = new byte[BitmapBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(bitmap, shards);
        return bitmap;
    }

    public static bool TryParseBitmap(ReadOnlySpan<byte> encoded, out uint shards)
    {
        shards = 0;
        if (encoded.Length != BitmapBytes) return false;
        shards = BinaryPrimitives.ReadUInt32LittleEndian(encoded);
        return true;
    }

    private static byte[] StringMessage(string value)
    {
        List<byte> inner = [];
        WriteBytesField(inner, 1, Encoding.UTF8.GetBytes(value));
        return [.. inner];
    }

    private static string ReadStringMessage(ReadOnlySpan<byte> message)
    {
        string value = "";
        for (Reader reader = new(message); reader.Next(out int number, out int wireType);)
            if (number == 1 && wireType == 2) value = Utf8(reader.Bytes());
            else reader.Skip(wireType);
        return value;
    }

    /// <summary>Reads the set member of a oneof whose members are length-delimited fields 1 to <paramref name="members"/>.</summary>
    private static int ReadOneof(ReadOnlySpan<byte> message, int members, out ReadOnlySpan<byte> inner)
    {
        int field = 0;
        inner = default;
        for (Reader reader = new(message); reader.Next(out int number, out int wireType);)
        {
            if (number >= 1 && number <= members && wireType == 2)
            {
                field = number;
                inner = reader.Bytes();
            }
            else reader.Skip(wireType);
        }
        if (field == 0) throw new LeanBroadcastFormatException("Frame has no message");
        return field;
    }

    private static byte[] Message(int field, List<byte> inner) => Message(field, inner.ToArray());

    private static byte[] Message(int field, byte[] inner)
    {
        List<byte> outer = [];
        WriteBytesField(outer, field, inner);
        return [.. outer];
    }

    private static string Utf8(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaxStringBytes) throw new LeanBroadcastFormatException("String field is too long");
        try { return StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { throw new LeanBroadcastFormatException("String field is not UTF-8"); }
    }

    private static void WriteVarintField(List<byte> output, int field, ulong value)
    {
        WriteVarint(output, (ulong)(field << 3));
        WriteVarint(output, value);
    }

    private static void WriteBytesField(List<byte> output, int field, ReadOnlySpan<byte> value)
    {
        WriteVarint(output, (ulong)(field << 3 | 2));
        WriteVarint(output, (ulong)value.Length);
        foreach (byte b in value) output.Add(b);
    }

    private static void WriteVarint(List<byte> output, ulong value)
    {
        while (value >= 0x80)
        {
            output.Add((byte)(value | 0x80));
            value >>= 7;
        }
        output.Add((byte)value);
    }

    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private ReadOnlySpan<byte> _data = data;

        public bool Next(out int number, out int wireType)
        {
            number = wireType = 0;
            if (_data.IsEmpty) return false;
            ulong tag = Varint();
            number = (int)Math.Min(tag >> 3, int.MaxValue);
            wireType = (int)(tag & 7);
            if (number == 0) throw new LeanBroadcastFormatException("Field number zero");
            return true;
        }

        public ulong Varint()
        {
            ulong value = 0;
            for (int shift = 0; shift < 64; shift += 7)
            {
                if (_data.IsEmpty) throw new LeanBroadcastFormatException("Truncated varint");
                byte next = _data[0];
                _data = _data[1..];
                value |= (ulong)(next & 0x7f) << shift;
                if (next < 0x80) return value;
            }
            throw new LeanBroadcastFormatException("Varint exceeds 64 bits");
        }

        public ReadOnlySpan<byte> Bytes()
        {
            ulong length = Varint();
            if (length > (ulong)_data.Length) throw new LeanBroadcastFormatException("Truncated length-delimited field");
            ReadOnlySpan<byte> value = _data[..(int)length];
            _data = _data[(int)length..];
            return value;
        }

        public void Skip(int wireType)
        {
            switch (wireType)
            {
                case 0: Varint(); break;
                case 1: Fixed(8); break;
                case 2: Bytes(); break;
                case 5: Fixed(4); break;
                default: throw new LeanBroadcastFormatException($"Unsupported wire type {wireType}");
            }
        }

        private void Fixed(int size)
        {
            if (_data.Length < size) throw new LeanBroadcastFormatException("Truncated fixed field");
            _data = _data[size..];
        }
    }
}
