// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.Intrinsics;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Network.Kademlia;

public static class Hash256XorUtils
{
    public static int CalculateDistance(ValueHash256 h1, ValueHash256 h2)
    {
        var zeros = 0;
        for (var i = 0; i < 32; i += 1)
        {
            var b1 = h1.Bytes[i];
            var b2 = h2.Bytes[i];
            var xord = (byte)(b1 ^ b2);
            if (xord == 0)
            {
                zeros += 8;
                continue;
            }

            var nonZeroPostfix = 1;
            while ((xord >>= 1) != 0)
            {
                nonZeroPostfix++;
            }
            zeros += 8 - nonZeroPostfix;

            break;
        }

        return MaxDistance - zeros;
    }

    public static UInt256 CalculateDistanceUInt256(ValueHash256 h1, ValueHash256 h2)
    {
        ValueHash256 xored = XorDistance(h1, h2);
        // TODO: Make this more efficirent/simd it.
        for (var i = 0; i < 32; i++)
        {
            xored.BytesAsSpan[i] = (byte)(h1.BytesAsSpan[i] ^ h2.BytesAsSpan[i]);
        }

        var XORed = new UInt256(xored.BytesAsSpan, true);
        return XORed;
    }

    public static int MaxDistance => 256;

    public static ValueHash256 GetRandomHashAtDistance(ValueHash256 currentHash, int distance)
    {
        return GetRandomHashAtDistance(currentHash, distance, Random.Shared);
    }

    public static ValueHash256 GetRandomHashAtDistance(ValueHash256 currentHash, int distance, Random random)
    {
        // TODO: Just add a min/max range per bucket and randomized between them.
        if (distance == MaxDistance)
            return currentHash;

        var randomized = new ValueHash256();
        random.NextBytes(randomized.BytesAsSpan);
        return CopyForRandom(currentHash, randomized, MaxDistance - distance);
    }

    public static int Compare(ValueHash256 a, ValueHash256 b, ValueHash256 c)
    {
        var ac = new ValueHash256();
        (new Vector<byte>(a.BytesAsSpan) ^ new Vector<byte>(c.BytesAsSpan)).CopyTo(ac.BytesAsSpan);

        var bc = new ValueHash256();
        (new Vector<byte>(b.BytesAsSpan) ^ new Vector<byte>(c.BytesAsSpan)).CopyTo(bc.BytesAsSpan);

        return ac.CompareTo(bc);
    }

    public static ValueHash256 CopyForRandom(ValueHash256 currentHash, ValueHash256 randomizedHash, int distance)
    {
        if (distance >= 256) return currentHash;

        currentHash.Bytes[0..(distance / 8)].CopyTo(randomizedHash.BytesAsSpan);

        var remainingBit = distance % 8;
        var remainingBitByte = distance / 8;
        var mask = (byte)~((1 << 8 - remainingBit) - 1);
        var randomized = randomizedHash.BytesAsSpan[remainingBitByte];
        var original = currentHash.BytesAsSpan[remainingBitByte];
        randomizedHash.BytesAsSpan[remainingBitByte] = (byte)(original & mask | randomized & ~mask);

        if (distance <= 255)
        {
            // So it always assume that the next bucket (the closer one) is always populated and therefore,
            // the bits here for that distance must not be the same as in currentHash.
            var nextBit = distance % 8;
            var nextBitByte = distance / 8;
            mask = (byte)(1 << 7 - nextBit);
            randomized = randomizedHash.BytesAsSpan[nextBitByte];
            var opposite = (byte)~currentHash.BytesAsSpan[nextBitByte];

            var final = (byte)(opposite & mask | randomized & ~mask);
            randomizedHash.BytesAsSpan[nextBitByte] = final;
        }

        return randomizedHash;
    }

    public static ValueHash256 XorDistance(ValueHash256 hash1, ValueHash256 hash2)
    {
        var xorBytes = new byte[hash1.Bytes.Length];
        for (var i = 0; i < xorBytes.Length; i++)
        {
            xorBytes[i] = (byte)(hash1.Bytes[i] ^ hash2.Bytes[i]);
        }
        return new ValueHash256(xorBytes);
    }

}
