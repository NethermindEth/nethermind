// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;
using Nethermind.Core.Crypto;
using Nethermind.Network.Discovery.Kademlia;

namespace Nethermind.Network.Discovery.Portal;

public class IEnrDistanceCalculator : IDistanceCalculator<IEnr>
{
    private readonly Hash256DistanceCalculator _baseDistanceCalculator = new();

    public int CalculateDistance(IEnr h1, IEnr h2)
    {
        return _baseDistanceCalculator.CalculateDistance(new ValueHash256(h1.NodeId), new ValueHash256(h2.NodeId));
    }

    public int MaxDistance => 256;
    public IEnr RandomizeHashAtDistance(IEnr hash, int distance)
    {
        return new ContentEnr(_baseDistanceCalculator.RandomizeHashAtDistance(new ValueHash256(hash.NodeId), distance).ToByteArray());
    }

    public int Compare(IEnr a, IEnr b, IEnr target)
    {
        return _baseDistanceCalculator.Compare(new ValueHash256(a.NodeId), new ValueHash256(b.NodeId), new ValueHash256(target.NodeId));
    }
}
