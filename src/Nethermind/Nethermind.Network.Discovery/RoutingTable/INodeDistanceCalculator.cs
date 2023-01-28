// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery.RoutingTable;

public interface INodeDistanceCalculator
{
    int CalculateDistance(byte[] sourceId, byte[] destinationId);
}
