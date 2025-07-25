// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using Nethermind.Core.Crypto;

namespace Nethermind.Network.Discovery.Kademlia;

public static class Hash256XorUtils
{
    public static int CalculateLogDistance(ValueHash256 h1, ValueHash256 h2)
    {
        ValueHash256 xor = XorDistance(h1, h2);
        int zeros = 0;
        for (int i = 0; i < 32; i += 1)
        {
            byte xord = xor.Bytes[i];
            if (xord == 0)
            {
                zeros += 8;
                continue;
            }

            int nonZeroPostfix = 1;
            while ((xord >>= 1) != 0)
            {
                nonZeroPostfix++;
            }
            zeros += 8 - nonZeroPostfix;

            break;
        }
        return MaxDistance - zeros;
    }

    public static int MaxDistance => 256;

    public static int Compare(ValueHash256 a, ValueHash256 b, ValueHash256 c)
    {
        ValueHash256 ac = XorDistance(a, c);
        ValueHash256 bc = XorDistance(b, c);
        return ac.CompareTo(bc);
    }

    public static ValueHash256 XorDistance(ValueHash256 hash1, ValueHash256 hash2)
    {
        ValueHash256 bc = new ValueHash256();
        (new Vector<byte>(hash1.BytesAsSpan) ^ new Vector<byte>(hash2.BytesAsSpan)).CopyTo(bc.BytesAsSpan);
        return bc;
    }

    public static ValueHash256 GetRandomHashAtDistance(ValueHash256 currentHash, int distance)
    {
        return GetRandomHashAtDistance(currentHash, distance, Random.Shared);
    }

    public static ValueHash256 GetRandomHashAtDistance(ValueHash256 currentHash, int distance, Random random)
    {
        // TODO: Just add a min/max range per bucket and randomized between them.
        if (distance == MaxDistance)
        {
            return currentHash;
        }

        ValueHash256 randomized = new ValueHash256();
        random.NextBytes(randomized.BytesAsSpan);
        return CopyForRandom(currentHash, randomized, MaxDistance - distance);
    }

    private static ValueHash256 CopyForRandom(ValueHash256 currentHash, ValueHash256 randomizedHash, int distance)
    {
        if (distance >= 256) return currentHash;

        currentHash.Bytes[0..(distance / 8)].CopyTo(randomizedHash.BytesAsSpan);

        int remainingBit = distance % 8;
        int remainingBitByte = distance / 8;
        byte mask = (byte)(~((1 << (8 - remainingBit)) - 1));
        byte randomized = randomizedHash.BytesAsSpan[remainingBitByte];
        byte original = currentHash.BytesAsSpan[remainingBitByte];
        randomizedHash.BytesAsSpan[remainingBitByte] = (byte)((original & mask) | (randomized & (~mask)));

        if (distance <= 255)
        {
            // So it always assume that the next bucket (the closer one) is always populated and therefore,
            // the bits here for that distance must not be the same as in currentHash.
            int nextBit = distance % 8;
            int nextBitByte = distance / 8;
            mask = (byte)(1 << (7 - nextBit));
            randomized = randomizedHash.BytesAsSpan[nextBitByte];
            byte opposite = (byte)~(currentHash.BytesAsSpan[nextBitByte]);

            byte final = (byte)((opposite & mask) | (randomized & ~(mask)));
            randomizedHash.BytesAsSpan[nextBitByte] = final;
        }

        return randomizedHash;
    }
}
