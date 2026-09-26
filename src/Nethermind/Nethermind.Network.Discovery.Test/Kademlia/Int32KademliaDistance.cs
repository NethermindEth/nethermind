// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using Nethermind.Kademlia;

namespace Nethermind.Network.Discovery.Test.Kademlia;

internal sealed class Int32KademliaDistance : IKademliaDistance<int>
{
    public static Int32KademliaDistance Instance { get; } = new();

    public int MaxDistance => 32;

    public int Zero => 0;

    public int CalculateLogDistance(int left, int right)
    {
        uint distance = (uint)(left ^ right);
        return distance == 0 ? 0 : MaxDistance - BitOperations.LeadingZeroCount(distance);
    }

    public int Compare(int left, int right, int target)
        => ((uint)(left ^ target)).CompareTo((uint)(right ^ target));

    public bool GetBit(int key, int index)
        => ((uint)key & (1u << (31 - index))) != 0;

    public int SetBit(int key, int index)
        => key | (int)(1u << (31 - index));
}
