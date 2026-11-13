// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;
using Nethermind.Kademlia;

namespace Nethermind.Network.Discovery.Kademlia;

/// <summary>
/// Kademlia XOR-distance operations for Nethermind's 256-bit value hash type.
/// </summary>
public sealed class ValueHash256KademliaDistance : IKademliaDistance<ValueHash256>
{
    /// <summary>
    /// Shared stateless instance.
    /// </summary>
    public static ValueHash256KademliaDistance Instance { get; } = new();

    /// <inheritdoc/>
    public int MaxDistance => ValueHash256.MemorySize * 8;

    /// <inheritdoc/>
    public ValueHash256 Zero => default;

    /// <inheritdoc/>
    public int CalculateLogDistance(ValueHash256 left, ValueHash256 right)
    {
        ReadOnlySpan<byte> leftBytes = left.Bytes;
        ReadOnlySpan<byte> rightBytes = right.Bytes;
        for (int i = 0; i < ValueHash256.MemorySize; i += sizeof(ulong))
        {
            ulong xor = BinaryPrimitives.ReadUInt64BigEndian(leftBytes[i..]) ^ BinaryPrimitives.ReadUInt64BigEndian(rightBytes[i..]);
            if (xor != 0)
            {
                return MaxDistance - i * 8 - BitOperations.LeadingZeroCount(xor);
            }
        }

        return 0;
    }

    /// <inheritdoc/>
    public int Compare(ValueHash256 left, ValueHash256 right, ValueHash256 target)
    {
        ReadOnlySpan<byte> leftBytes = left.Bytes;
        ReadOnlySpan<byte> rightBytes = right.Bytes;
        ReadOnlySpan<byte> targetBytes = target.Bytes;
        for (int i = 0; i < ValueHash256.MemorySize; i += sizeof(ulong))
        {
            ulong targetWord = BinaryPrimitives.ReadUInt64BigEndian(targetBytes[i..]);
            ulong leftDistance = BinaryPrimitives.ReadUInt64BigEndian(leftBytes[i..]) ^ targetWord;
            ulong rightDistance = BinaryPrimitives.ReadUInt64BigEndian(rightBytes[i..]) ^ targetWord;
            if (leftDistance != rightDistance)
            {
                return leftDistance < rightDistance ? -1 : 1;
            }
        }

        return 0;
    }

    /// <inheritdoc/>
    public bool GetBit(ValueHash256 key, int index)
    {
        int byteIndex = index / 8;
        int bitIndex = index % 8;
        return (key.Bytes[byteIndex] & (1 << (7 - bitIndex))) != 0;
    }

    /// <inheritdoc/>
    [SkipLocalsInit]
    public ValueHash256 SetBit(ValueHash256 key, int index)
    {
        Span<byte> bytes = stackalloc byte[ValueHash256.MemorySize];
        key.Bytes.CopyTo(bytes);
        bytes[index / 8] |= (byte)(1 << (7 - (index % 8)));
        return new ValueHash256(bytes);
    }
}
