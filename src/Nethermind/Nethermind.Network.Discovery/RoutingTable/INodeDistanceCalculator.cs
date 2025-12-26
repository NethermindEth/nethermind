// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Network.Discovery.RoutingTable;

public interface INodeDistanceCalculator
{
    int CalculateDistance(Hash256 sourceId, Hash256 destinationId);
}
