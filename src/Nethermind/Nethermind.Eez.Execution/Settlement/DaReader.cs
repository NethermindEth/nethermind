// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;

namespace Nethermind.Eez.Execution.Settlement;

/// <summary>Reads the fields of a DA payload in order, failing with <see cref="EezSettlementFailure.InvalidDaPayload"/>.</summary>
internal struct DaReader(ReadOnlyMemory<byte> data)
{
    private const int MaxVarintBytes = 5;

    private int _position;

    public readonly int Remaining => data.Length - _position;

    public ReadOnlyMemory<byte> Take(string field, int length)
    {
        if (length > Remaining)
        {
            throw Invalid($"truncated while reading {field}");
        }

        ReadOnlyMemory<byte> slice = data.Slice(_position, length);
        _position += length;
        return slice;
    }

    public byte Byte(string field) => Take(field, 1).Span[0];

    public ulong UInt64(string field) => BinaryPrimitives.ReadUInt64LittleEndian(Take(field, sizeof(ulong)).Span);

    public void Expect(byte message)
    {
        if (Byte("message type") != message)
        {
            throw Invalid($"expected message type {message}");
        }
    }

    public ReadOnlyMemory<byte> Bytes(string field, bool padded) => Take(field, Varint(field, padded));

    /// <summary>A <c>uint32</c> in one to five bytes; unless <paramref name="padded"/>, only its shortest form.</summary>
    public int Varint(string field, bool padded)
    {
        uint value = UInt32Varint(field, padded);
        return value <= int.MaxValue ? (int)value : throw Invalid($"{field} {value} is out of range");
    }

    /// <summary>A <c>uint32</c> in one to five bytes; unless <paramref name="padded"/>, only its shortest form.</summary>
    public uint UInt32Varint(string field, bool padded)
    {
        uint value = 0;
        for (int i = 0; i < MaxVarintBytes; i++)
        {
            byte current = Byte(field);
            uint group = current & 0x7fu;
            if (i == MaxVarintBytes - 1 && group > 0x0f)
            {
                throw Invalid($"varint out of range while reading {field}");
            }

            value |= group << (7 * i);
            if ((current & 0x80) == 0)
            {
                if (!padded && i > 0 && group == 0)
                {
                    throw Invalid($"padded varint while reading {field}");
                }

                return value;
            }
        }

        throw Invalid($"varint too long while reading {field}");
    }

    public readonly void EnsurePlausible(string field, long count, int bytesPerElement)
    {
        if (count > Remaining / bytesPerElement)
        {
            throw Invalid($"{field} {count} exceeds what the {Remaining} remaining bytes can encode");
        }
    }

    public static EezSettlementException Invalid(string reason) => new(EezSettlementFailure.InvalidDaPayload, $"Invalid DA payload: {reason}.");
}
