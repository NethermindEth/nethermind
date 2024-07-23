// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;
using Nethermind.Network.Discovery.Kademlia;

namespace Nethermind.Network.Discovery.Portal;

public class IEnrDistanceCalculator : IDistanceCalculator<IEnr>
{
    public int CalculateDistance(IEnr h1, IEnr h2)
    {
        throw new NotImplementedException();
    }

    public int MaxDistance { get; }
    public IEnr RandomizeHashAtDistance(IEnr hash, int distance)
    {
        throw new NotImplementedException();
    }

    public int Compare(IEnr a, IEnr b, IEnr target)
    {
        throw new NotImplementedException();
    }
}
