// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Eez.Execution.Settlement;

/// <summary>
/// Reads ABI words at absolute positions and accepts only the canonical encoding: values in range, zero padding,
/// and every dynamic offset pointing exactly where the canonical encoder would place the data.
/// </summary>
internal readonly ref struct AbiReader(ReadOnlySpan<byte> data)
{
    private const int Word = AbiWord.Size;

    private readonly ReadOnlySpan<byte> _data = data;

    public int Length => _data.Length;

    public ValueHash256 ReadHash(int position) => new(WordAt(position));

    public ulong ReadUInt64(int position) => ReadUnsigned(position, sizeof(ulong), "uint64");

    public ushort ReadUInt16(int position) => (ushort)ReadUnsigned(position, sizeof(ushort), "uint16");

    public bool ReadBool(int position) => ReadUnsigned(position, 1, "bool") switch
    {
        0 => false,
        1 => true,
        _ => throw new EezAbiException($"Non-canonical bool at {position}."),
    };

    public Address ReadAddress(int position)
    {
        ReadOnlySpan<byte> word = WordAt(position);
        EnsureZero(word[..(Word - Address.Size)], position, "address");
        return new Address(word[(Word - Address.Size)..]);
    }

    public UInt256 ReadUInt256(int position) => new(WordAt(position), isBigEndian: true);

    public Int256.Int256 ReadInt256(int position) => new(ReadUInt256(position));

    /// <summary>Checks that the offset at <paramref name="position"/>, relative to <paramref name="tupleStart"/>, is <paramref name="tail"/>.</summary>
    public void ExpectOffset(int position, int tupleStart, int tail)
    {
        if (ReadLength(position) != tail - tupleStart)
        {
            throw new EezAbiException($"Non-canonical offset at {position}: expected {tail - tupleStart}.");
        }
    }

    /// <summary>Reads an element count or byte length, bounded by the data so it cannot drive a large allocation.</summary>
    public int ReadLength(int position)
    {
        ulong value = ReadUnsigned(position, sizeof(uint), "length");
        return value <= (ulong)_data.Length
            ? (int)value
            : throw new EezAbiException($"Length {value} at {position} exceeds the data.");
    }

    public byte[] ReadBytes(int position, out int size)
    {
        int length = ReadLength(position);
        int padded = AbiWord.PaddedLength(length);
        int start = position + Word;
        EnsureAvailable(start, padded);
        EnsureZero(_data.Slice(start + length, padded - length), start + length, "bytes padding");
        size = Word + padded;
        return _data.Slice(start, length).ToArray();
    }

    /// <summary>Reads a count of elements that each occupy at least <paramref name="minElementSize"/> bytes after it.</summary>
    public int ReadCount(int position, int minElementSize)
    {
        int count = ReadLength(position);
        EnsureAvailable(position + Word, count * minElementSize);
        return count;
    }

    private ulong ReadUnsigned(int position, int size, string type)
    {
        ReadOnlySpan<byte> word = WordAt(position);
        EnsureZero(word[..(Word - size)], position, type);
        ulong value = 0;
        foreach (byte b in word[(Word - size)..])
        {
            value = (value << 8) | b;
        }

        return value;
    }

    private ReadOnlySpan<byte> WordAt(int position)
    {
        EnsureAvailable(position, Word);
        return _data.Slice(position, Word);
    }

    private void EnsureAvailable(int position, int length)
    {
        if (position < 0 || length < 0 || (long)position + length > _data.Length)
        {
            throw new EezAbiException($"ABI data ends before {position + (long)length}.");
        }
    }

    private static void EnsureZero(ReadOnlySpan<byte> padding, int position, string type)
    {
        if (!padding.IsZero())
        {
            throw new EezAbiException($"Non-canonical {type} at {position}.");
        }
    }
}
