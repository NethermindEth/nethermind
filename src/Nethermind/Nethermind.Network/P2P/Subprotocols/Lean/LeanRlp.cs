// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Numerics;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Lean;

/// <summary>Strict reader for EIP-8437 message data: canonical RLP with exact field counts and bounded nesting.</summary>
/// <remarks>
/// Rejects nonminimal length prefixes, single bytes below 0x80 wrapped in a prefix, integers with leading zeros,
/// wrong string/list types and trailing bytes. Every list descent is charged against
/// <see cref="LeanProtocol.MaxRlpDepth"/>.
/// </remarks>
public ref struct LeanRlpReader
{
    private readonly ReadOnlySpan<byte> _data;
    private readonly int _depth;
    private int _position;

    public LeanRlpReader(ReadOnlySpan<byte> data) : this(data, 0) { }

    private LeanRlpReader(ReadOnlySpan<byte> data, int depth)
    {
        _data = data;
        _depth = depth;
        _position = 0;
    }

    public readonly bool HasMore => _position < _data.Length;

    /// <summary>Reads a list and returns a reader over its content; the caller must call <see cref="End"/> on it.</summary>
    public LeanRlpReader ReadList()
    {
        if (_depth + 1 > LeanProtocol.MaxRlpDepth) throw new RlpException("RLP nesting exceeds MAX_RLP_DEPTH");
        (bool isList, int offset, int length) = ReadHeader();
        if (!isList) throw new RlpException("Expected an RLP list");
        return new LeanRlpReader(_data.Slice(offset, length), _depth + 1);
    }

    /// <summary>Reads a byte string's content.</summary>
    public ReadOnlySpan<byte> ReadBytes()
    {
        (bool isList, int offset, int length) = ReadHeader();
        if (isList) throw new RlpException("Expected an RLP string");
        return _data.Slice(offset, length);
    }

    /// <summary>Reads one complete item, string or list, and returns its encoding including the prefix.</summary>
    public ReadOnlySpan<byte> ReadItem()
    {
        int start = _position;
        (bool isList, int offset, int length) = ReadHeader();
        if (isList)
        {
            LeanRlpReader nested = new(_data.Slice(offset, length), _depth + 1);
            if (_depth + 1 > LeanProtocol.MaxRlpDepth) throw new RlpException("RLP nesting exceeds MAX_RLP_DEPTH");
            while (nested.HasMore) nested.ReadItem();
        }
        return _data[start.._position];
    }

    public ulong ReadUInt64()
    {
        ReadOnlySpan<byte> bytes = ReadBytes();
        if (bytes.Length > sizeof(ulong)) throw new RlpException("Integer exceeds 64 bits");
        if (bytes.Length > 0 && bytes[0] == 0) throw new RlpException("Integer has a leading zero");
        ulong value = 0;
        foreach (byte b in bytes) value = value << 8 | b;
        return value;
    }

    public uint ReadUInt32()
    {
        ulong value = ReadUInt64();
        if (value > uint.MaxValue) throw new RlpException("Integer exceeds 32 bits");
        return (uint)value;
    }

    public byte ReadUInt8()
    {
        ulong value = ReadUInt64();
        if (value > byte.MaxValue) throw new RlpException("Integer exceeds 8 bits");
        return (byte)value;
    }

    /// <summary>Reads an unsigned integer below <c>2**256</c>.</summary>
    public UInt256 ReadUInt256()
    {
        ReadOnlySpan<byte> bytes = ReadBytes();
        if (bytes.Length > 32) throw new RlpException("Integer exceeds 256 bits");
        if (bytes.Length > 0 && bytes[0] == 0) throw new RlpException("Integer has a leading zero");
        return new UInt256(bytes, true);
    }

    public ValueHash256 ReadHash()
    {
        ReadOnlySpan<byte> bytes = ReadBytes();
        if (bytes.Length != ValueHash256.MemorySize) throw new RlpException("Hash must be exactly 32 bytes");
        return new ValueHash256(bytes);
    }

    public void ReadEmptyBytes()
    {
        if (ReadBytes().Length != 0) throw new RlpException("Expected an empty byte string");
    }

    /// <summary>Counts the remaining items without decoding them, failing once <paramref name="max"/> is exceeded.</summary>
    public readonly int CountRemaining(int max)
    {
        LeanRlpReader copy = this;
        int count = 0;
        while (copy.HasMore)
        {
            if (++count > max) throw new RlpException($"List exceeds {max} items");
            copy.ReadHeader();
        }
        return count;
    }

    public readonly void End()
    {
        if (HasMore) throw new RlpException("Unexpected trailing RLP data");
    }

    private (bool IsList, int Offset, int Length) ReadHeader()
    {
        if (_position >= _data.Length) throw new RlpException("RLP data is truncated");
        byte prefix = _data[_position];
        bool isList = prefix >= 0xc0;
        int offset;
        int length;
        if (prefix < 0x80)
        {
            offset = _position;
            length = 1;
        }
        else if (prefix <= 0xb7 || (prefix >= 0xc0 && prefix <= 0xf7))
        {
            length = prefix - (isList ? 0xc0 : 0x80);
            offset = _position + 1;
            if (!isList && length == 1 && offset < _data.Length && _data[offset] < 0x80)
                throw new RlpException("Single byte below 0x80 must not carry a prefix");
        }
        else
        {
            int lengthOfLength = prefix - (isList ? 0xf7 : 0xb7);
            if (lengthOfLength > 4 || _position + 1 + lengthOfLength > _data.Length) throw new RlpException("Invalid RLP length prefix");
            ReadOnlySpan<byte> lengthBytes = _data.Slice(_position + 1, lengthOfLength);
            if (lengthBytes[0] == 0) throw new RlpException("RLP length has a leading zero");
            uint declared = 0;
            foreach (byte b in lengthBytes) declared = declared << 8 | b;
            if (declared < 56 || declared > int.MaxValue) throw new RlpException("Nonminimal RLP length prefix");
            length = (int)declared;
            offset = _position + 1 + lengthOfLength;
        }
        if (length > _data.Length - offset) throw new RlpException("RLP data is truncated");
        _position = offset + length;
        return (isList, offset, length);
    }
}

/// <summary>Canonical RLP writer for EIP-8437 message data.</summary>
public static class LeanRlp
{
    public static int LengthOfBytes(ReadOnlySpan<byte> value) =>
        value.Length == 1 && value[0] < 0x80 ? 1 : LengthOfPrefix(value.Length) + value.Length;

    public static int LengthOfUInt(ulong value) => value < 0x80 ? 1 : 1 + ByteCount(value);

    public static int LengthOfList(int contentLength) => LengthOfPrefix(contentLength) + contentLength;

    public static int LengthOfPrefix(int contentLength) => contentLength < 56 ? 1 : 1 + ByteCount((ulong)contentLength);

    public static int WriteBytes(Span<byte> destination, ReadOnlySpan<byte> value)
    {
        if (value.Length == 1 && value[0] < 0x80)
        {
            destination[0] = value[0];
            return 1;
        }
        int prefix = WritePrefix(destination, value.Length, 0x80);
        value.CopyTo(destination[prefix..]);
        return prefix + value.Length;
    }

    public static int WriteUInt(Span<byte> destination, ulong value)
    {
        if (value == 0)
        {
            destination[0] = 0x80;
            return 1;
        }
        if (value < 0x80)
        {
            destination[0] = (byte)value;
            return 1;
        }
        int count = ByteCount(value);
        destination[0] = (byte)(0x80 + count);
        for (int i = 0; i < count; i++) destination[count - i] = (byte)(value >> (8 * i));
        return 1 + count;
    }

    public static int WriteListPrefix(Span<byte> destination, int contentLength) => WritePrefix(destination, contentLength, 0xc0);

    /// <summary>Writes the prefix of a string that is not a single byte below 0x80.</summary>
    public static int WriteStringPrefix(Span<byte> destination, int length) => WritePrefix(destination, length, 0x80);

    public static byte[] EncodeBytes(ReadOnlySpan<byte> value)
    {
        byte[] result = new byte[LengthOfBytes(value)];
        WriteBytes(result, value);
        return result;
    }

    public static byte[] EncodeUInt256(in UInt256 value)
    {
        if (value.IsZero) return [0x80];
        Span<byte> bytes = stackalloc byte[32];
        value.ToBigEndian(bytes);
        return EncodeBytes(bytes.WithoutLeadingZeros());
    }

    public static byte[] EncodeUInt(ulong value)
    {
        byte[] result = new byte[LengthOfUInt(value)];
        WriteUInt(result, value);
        return result;
    }

    /// <summary>Wraps already encoded items in a list.</summary>
    public static byte[] EncodeList(params ReadOnlySpan<byte[]> items)
    {
        int content = 0;
        foreach (byte[] item in items) content += item.Length;
        byte[] result = new byte[LengthOfList(content)];
        int position = WriteListPrefix(result, content);
        foreach (byte[] item in items)
        {
            item.CopyTo(result, position);
            position += item.Length;
        }
        return result;
    }

    private static int WritePrefix(Span<byte> destination, int length, byte offset)
    {
        if (length < 56)
        {
            destination[0] = (byte)(offset + length);
            return 1;
        }
        int count = ByteCount((ulong)length);
        destination[0] = (byte)(offset + 55 + count);
        for (int i = 0; i < count; i++) destination[count - i] = (byte)(length >> (8 * i));
        return 1 + count;
    }

    private static int ByteCount(ulong value) => (64 - BitOperations.LeadingZeroCount(value) + 7) / 8;

    /// <summary>Writes a fixed-width big-endian integer used by commitment preimages.</summary>
    internal static void WriteU32(Span<byte> destination, uint value) => BinaryPrimitives.WriteUInt32BigEndian(destination, value);
}
