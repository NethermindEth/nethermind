// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;

namespace Nethermind.Kademlia;

public static class Hash256XorUtils
{
    public static int CalculateLogDistance(KademliaHash h1, KademliaHash h2)
    {
        KademliaHash xor = XorDistance(h1, h2);
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

    public const int MaxDistance = 256;

    public static int Compare(KademliaHash a, KademliaHash b, KademliaHash c)
    {
        KademliaHash ac = XorDistance(a, c);
        KademliaHash bc = XorDistance(b, c);
        return ac.CompareTo(bc);
    }

    public static KademliaHash XorDistance(KademliaHash hash1, KademliaHash hash2)
    {
        ReadOnlySpan<byte> hash1Bytes = hash1.Bytes;
        ReadOnlySpan<byte> hash2Bytes = hash2.Bytes;
        Span<byte> result = stackalloc byte[KademliaHash.Length];

        int i = 0;
        for (; i <= result.Length - Vector<byte>.Count; i += Vector<byte>.Count)
        {
            (new Vector<byte>(hash1Bytes[i..]) ^ new Vector<byte>(hash2Bytes[i..])).CopyTo(result[i..]);
        }

        for (; i < result.Length; i++)
        {
            result[i] = (byte)(hash1Bytes[i] ^ hash2Bytes[i]);
        }

        return KademliaHash.FromBytes(result);
    }

    public static KademliaHash GetRandomHashAtDistance(KademliaHash currentHash, int distance) => GetRandomHashAtDistance(currentHash, distance, Random.Shared);

    public static KademliaHash GetRandomHashAtDistance(KademliaHash currentHash, int distance, Random random)
    {
        // TODO: Just add a min/max range per bucket and randomized between them.
        if (distance == MaxDistance)
        {
            return currentHash;
        }

        Span<byte> randomized = stackalloc byte[KademliaHash.Length];
        random.NextBytes(randomized);
        return CopyForRandom(currentHash, randomized, MaxDistance - distance);
    }

    private static KademliaHash CopyForRandom(KademliaHash currentHash, Span<byte> randomizedHash, int distance)
    {
        if (distance >= 256) return currentHash;

        currentHash.Bytes[0..(distance / 8)].CopyTo(randomizedHash);

        int remainingBit = distance % 8;
        int remainingBitByte = distance / 8;
        byte mask = (byte)(~((1 << (8 - remainingBit)) - 1));
        byte randomized = randomizedHash[remainingBitByte];
        byte original = currentHash.Bytes[remainingBitByte];
        randomizedHash[remainingBitByte] = (byte)((original & mask) | (randomized & (~mask)));

        if (distance <= 255)
        {
            // So it always assume that the next bucket (the closer one) is always populated and therefore,
            // the bits here for that distance must not be the same as in currentHash.
            int nextBit = distance % 8;
            int nextBitByte = distance / 8;
            mask = (byte)(1 << (7 - nextBit));
            randomized = randomizedHash[nextBitByte];
            byte opposite = (byte)~(currentHash.Bytes[nextBitByte]);

            byte final = (byte)((opposite & mask) | (randomized & ~(mask)));
            randomizedHash[nextBitByte] = final;
        }

        return KademliaHash.FromBytes(randomizedHash);
    }
}
