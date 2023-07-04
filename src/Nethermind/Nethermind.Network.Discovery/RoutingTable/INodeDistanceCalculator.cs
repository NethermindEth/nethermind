// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery.RoutingTable;

public interface INodeDistanceCalculator
{
    int CalculateDistance(ReadOnlySpan<byte> sourceId, ReadOnlySpan<byte> destinationId);
}
