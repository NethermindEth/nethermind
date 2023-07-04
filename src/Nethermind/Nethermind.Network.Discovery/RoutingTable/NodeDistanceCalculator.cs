// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.Config;

namespace Nethermind.Network.Discovery.RoutingTable;

public class NodeDistanceCalculator : INodeDistanceCalculator
{
    private readonly int _maxDistance;
    private readonly int _bitsPerHoop;

    public NodeDistanceCalculator(IDiscoveryConfig discoveryConfig)
    {
        _maxDistance = discoveryConfig.BucketsCount;
        _bitsPerHoop = discoveryConfig.BitsPerHop;
    }

    public int CalculateDistance(ReadOnlySpan<byte> sourceId, ReadOnlySpan<byte> destinationId)
    {
        int lowerLength = Math.Min(sourceId.Length, destinationId.Length);
        int distance = _maxDistance;

        for (int i = 0; i < lowerLength; i++)
        {
            byte b = (byte)(destinationId[i] ^ sourceId[i]);
            if (b == 0)
            {
                distance -= _bitsPerHoop;
            }
            else
            {
                int count = 0;
                for (int j = _bitsPerHoop - 1; j >= 0; j--)
                {
                    //why not b[j] == 0
                    if ((b & (1 << j)) == 0)
                    {
                        count++;
                    }
                    else
                    {
                        break;
                    }
                }
                distance -= count;
                break;
            }
        }

        return distance;
    }
}
