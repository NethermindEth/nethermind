/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using Nethermind.Network.Config;

namespace Nethermind.Network.Discovery.RoutingTable
{
    public class NodeDistanceCalculator : INodeDistanceCalculator
    {
        private readonly int _maxDistance;
        private readonly int _bitsPerHoop;

        public NodeDistanceCalculator(IDiscoveryConfig discoveryConfig)
        {
            _maxDistance = discoveryConfig.BucketsCount;
            _bitsPerHoop = discoveryConfig.BitsPerHop;
        }

        public int CalculateDistance(byte[] sourceId, byte[] destinationId)
        {
            var hash = new byte[destinationId.Length < sourceId.Length ? destinationId.Length : sourceId.Length];

            for (int i = 0; i < hash.Length; i++)
            {
                hash[i] = (byte)(destinationId[i] ^ sourceId[i]);
            }

            int distance = _maxDistance;

            for (int i = 0; i < hash.Length; i++)
            {
                var b = hash[i];
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
}