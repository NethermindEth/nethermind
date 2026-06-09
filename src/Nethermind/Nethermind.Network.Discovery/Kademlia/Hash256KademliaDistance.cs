// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;
using Nethermind.Kademlia;

namespace Nethermind.Network.Discovery.Kademlia;

/// <summary>
/// Kademlia XOR-distance operations for Nethermind's 256-bit hash type.
/// </summary>
public sealed class Hash256KademliaDistance : IKademliaDistance<Hash256>
{
    /// <summary>
    /// Shared stateless instance.
    /// </summary>
    public static Hash256KademliaDistance Instance { get; } = new();

    /// <inheritdoc/>
    public int MaxDistance => Hash256.Size * 8;

    /// <inheritdoc/>
    public Hash256 Zero => Hash256.Zero;

    /// <inheritdoc/>
    [SkipLocalsInit]
    public int CalculateLogDistance(Hash256 left, Hash256 right)
    {
        Span<byte> xorDistance = stackalloc byte[Hash256.Size];
        XorDistance(left.Bytes, right.Bytes, xorDistance);
        int zeros = 0;

        for (int i = 0; i < Hash256.Size; i++)
        {
            byte xor = xorDistance[i];
            if (xor == 0)
            {
                zeros += 8;
                continue;
            }

            int nonZeroPostfix = 1;
            while ((xor >>= 1) != 0)
            {
                nonZeroPostfix++;
            }

            zeros += 8 - nonZeroPostfix;
            break;
        }

        return MaxDistance - zeros;
    }

    /// <inheritdoc/>
    [SkipLocalsInit]
    public int Compare(Hash256 left, Hash256 right, Hash256 target)
    {
        Span<byte> leftDistance = stackalloc byte[Hash256.Size];
        Span<byte> rightDistance = stackalloc byte[Hash256.Size];
        ReadOnlySpan<byte> targetBytes = target.Bytes;
        XorDistance(left.Bytes, targetBytes, leftDistance);
        XorDistance(right.Bytes, targetBytes, rightDistance);

        return leftDistance.SequenceCompareTo(rightDistance);
    }

    /// <inheritdoc/>
    public bool GetBit(Hash256 key, int index)
    {
        int byteIndex = index / 8;
        int bitIndex = index % 8;
        return (key.Bytes[byteIndex] & (1 << (7 - bitIndex))) != 0;
    }

    /// <inheritdoc/>
    [SkipLocalsInit]
    public Hash256 SetBit(Hash256 key, int index)
    {
        Span<byte> bytes = stackalloc byte[Hash256.Size];
        key.Bytes.CopyTo(bytes);
        bytes[index / 8] |= (byte)(1 << (7 - (index % 8)));
        return new Hash256(bytes);
    }

    /// <summary>
    /// Creates a random 256-bit key at the requested XOR log distance from <paramref name="currentHash"/>.
    /// </summary>
    public Hash256 GetRandomHashAtDistance(Hash256 currentHash, int distance) =>
        GetRandomHashAtDistance(currentHash, distance, Random.Shared);

    /// <summary>
    /// Creates a random 256-bit key at the requested XOR log distance from <paramref name="currentHash"/>.
    /// </summary>
    [SkipLocalsInit]
    public Hash256 GetRandomHashAtDistance(Hash256 currentHash, int distance, Random random)
    {
        if ((uint)distance > MaxDistance)
        {
            throw new ArgumentOutOfRangeException(nameof(distance), distance, $"Distance must be between 0 and {MaxDistance}.");
        }

        Span<byte> randomized = stackalloc byte[Hash256.Size];
        random.NextBytes(randomized);
        return CopyForRandom(currentHash, randomized, MaxDistance - distance);
    }

    private Hash256 CopyForRandom(Hash256 currentHash, Span<byte> randomizedHash, int distance)
    {
        if (distance >= MaxDistance)
        {
            return currentHash;
        }

        currentHash.Bytes[..(distance / 8)].CopyTo(randomizedHash);

        int remainingBit = distance % 8;
        int remainingBitByte = distance / 8;
        byte mask = (byte)(~((1 << (8 - remainingBit)) - 1));
        byte randomized = randomizedHash[remainingBitByte];
        byte original = currentHash.Bytes[remainingBitByte];
        randomizedHash[remainingBitByte] = (byte)((original & mask) | (randomized & ~mask));

        if (distance <= MaxDistance - 1)
        {
            int nextBit = distance % 8;
            int nextBitByte = distance / 8;
            mask = (byte)(1 << (7 - nextBit));
            randomized = randomizedHash[nextBitByte];
            byte opposite = (byte)~currentHash.Bytes[nextBitByte];
            randomizedHash[nextBitByte] = (byte)((opposite & mask) | (randomized & ~mask));
        }

        return new Hash256(randomizedHash);
    }

    private static void XorDistance(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right, Span<byte> destination)
    {
        int i = 0;
        for (; i <= destination.Length - Vector<byte>.Count; i += Vector<byte>.Count)
        {
            (new Vector<byte>(left[i..]) ^ new Vector<byte>(right[i..])).CopyTo(destination[i..]);
        }

        for (; i < destination.Length; i++)
        {
            destination[i] = (byte)(left[i] ^ right[i]);
        }
    }
}
