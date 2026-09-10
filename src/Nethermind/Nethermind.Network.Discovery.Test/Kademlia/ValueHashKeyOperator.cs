// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Kademlia;

namespace Nethermind.Network.Discovery.Test.Kademlia;

internal sealed class ValueHashKeyOperator<TNode>(Func<TNode, ValueHash256> getKey) : IKeyOperator<ValueHash256, TNode, ValueHash256>
{
    public ValueHash256 GetKey(TNode node) => getKey(node);

    public ValueHash256 GetKeyHash(ValueHash256 key) => key;

    public ValueHash256 CreateRandomKeyAtDistance(ValueHash256 nodePrefix, int depth)
        => ValueHash256TestHelper.CreateRandomHashAtDistance(nodePrefix, depth, Random.Shared);
}

internal static class ValueHash256TestHelper
{
    public static ValueHash256 CreateRandomHashAtDistance(ValueHash256 currentHash, int distance, Random random)
    {
        const int maxDistance = ValueHash256.MemorySize * 8;
        if ((uint)distance > maxDistance)
        {
            throw new ArgumentOutOfRangeException(nameof(distance), distance, $"Distance must be between 0 and {maxDistance}.");
        }

        if (distance == 0)
        {
            return currentHash;
        }

        Span<byte> randomized = stackalloc byte[ValueHash256.MemorySize];
        random.NextBytes(randomized);

        int commonPrefixLength = maxDistance - distance;
        int differentByteIndex = commonPrefixLength / 8;
        int differentBitIndex = commonPrefixLength % 8;
        currentHash.Bytes[..differentByteIndex].CopyTo(randomized);

        byte prefixMask = (byte)(byte.MaxValue << (8 - differentBitIndex));
        randomized[differentByteIndex] = (byte)(
            (currentHash.Bytes[differentByteIndex] & prefixMask) |
            (randomized[differentByteIndex] & ~prefixMask));
        byte differentBitMask = (byte)(1 << (7 - differentBitIndex));
        randomized[differentByteIndex] = (byte)(
            (~currentHash.Bytes[differentByteIndex] & differentBitMask) |
            (randomized[differentByteIndex] & ~differentBitMask));

        return new ValueHash256(randomized);
    }
}
