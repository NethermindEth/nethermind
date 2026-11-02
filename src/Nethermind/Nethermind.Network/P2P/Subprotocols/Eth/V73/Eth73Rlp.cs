// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V73;

/// <summary>Strict reader for EIP-8437 <c>eth/73</c> message data: canonical RLP with exact field counts and bounded nesting.</summary>
/// <remarks>
/// Rejects nonminimal length prefixes, single bytes below 0x80 wrapped in a prefix, integers with leading zeros,
/// wrong string/list types and trailing bytes. Every list descent is charged against
/// <see cref="Eth73Protocol.MaxRlpDepth"/>.
/// </remarks>
public ref struct Eth73RlpReader
{
    private readonly ReadOnlySpan<byte> _data;
    private readonly int _depth;
    private int _position;

    public Eth73RlpReader(ReadOnlySpan<byte> data) : this(data, 0) { }

    private Eth73RlpReader(ReadOnlySpan<byte> data, int depth)
    {
        _data = data;
        _depth = depth;
        _position = 0;
    }

    public readonly bool HasMore => _position < _data.Length;

    /// <summary>Whether the next item is a list.</summary>
    public readonly bool IsListNext => HasMore && _data[_position] >= 0xc0;

    /// <summary>Reads a list and returns a reader over its content; the caller must call <see cref="End"/> on it.</summary>
    public Eth73RlpReader ReadList()
    {
        if (_depth + 1 > Eth73Protocol.MaxRlpDepth) throw new RlpException("RLP nesting exceeds MAX_RLP_DEPTH");
        (bool isList, int offset, int length) = ReadHeader();
        if (!isList) throw new RlpException("Expected an RLP list");
        return new Eth73RlpReader(_data.Slice(offset, length), _depth + 1);
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
            Eth73RlpReader nested = new(_data.Slice(offset, length), _depth + 1);
            if (_depth + 1 > Eth73Protocol.MaxRlpDepth) throw new RlpException("RLP nesting exceeds MAX_RLP_DEPTH");
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

    public ValueHash256 ReadHash()
    {
        ReadOnlySpan<byte> bytes = ReadBytes();
        if (bytes.Length != ValueHash256.MemorySize) throw new RlpException("Hash must be exactly 32 bytes");
        return new ValueHash256(bytes);
    }

    /// <summary>Counts the remaining items without decoding them, failing once <paramref name="max"/> is exceeded.</summary>
    public readonly int CountRemaining(int max)
    {
        Eth73RlpReader copy = this;
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

/// <summary>Canonical RLP writer for EIP-8437 <c>eth/73</c> message data.</summary>
public static class Eth73Rlp
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

    public static byte[] EncodeBytes(ReadOnlySpan<byte> value)
    {
        byte[] result = new byte[LengthOfBytes(value)];
        WriteBytes(result, value);
        return result;
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
}
