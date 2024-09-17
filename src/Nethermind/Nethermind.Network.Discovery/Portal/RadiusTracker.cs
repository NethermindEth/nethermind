// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Network.Discovery.Kademlia;

namespace Nethermind.Network.Discovery.Portal;

public class RadiusTracker (
    IEnrProvider enrProvider,
    ContentNetworkConfig config)
{
    private readonly ContentKeyHashProvider _contentKeyHashProvider = ContentKeyHashProvider.Instance;
    private readonly EnrNodeHashProvider _nodeHashProvider = EnrNodeHashProvider.Instance;
    private readonly LruCache<ValueHash256, UInt256> _distanceCache = new(1000, "");

    private readonly UInt256 _defaultRadius = config.DefaultPeerRadius;

    public void UpdatePeerRadius(IEnr node, UInt256 radius)
    {
        _distanceCache.Set(_nodeHashProvider.GetHash(node), radius);
    }

    public bool IsInRadius(IEnr node, ValueHash256 contentHash)
    {
        ValueHash256 nodeHash = new ValueHash256(node.NodeId);
        if (!_distanceCache.TryGet(nodeHash, out UInt256 nodeRadius))
        {
            nodeRadius = _defaultRadius;
        }

        return IsInRadius(nodeHash, contentHash, nodeRadius);
    }

    public bool IsContentInRadius(byte[] offerContentKey)
    {
        ValueHash256 contentHash = _contentKeyHashProvider.GetHash(offerContentKey);
        ValueHash256 nodeHash = _nodeHashProvider.GetHash(enrProvider.SelfEnr);

        return IsInRadius(nodeHash, contentHash, config.ContentRadius);
    }

    private bool IsInRadius(ValueHash256 nodeHash, ValueHash256 contentHash, UInt256 nodeRadius)
    {
        UInt256 distance = Hash256XORUtils.CalculateDistanceUInt256(contentHash, nodeHash);
        bool inRadius = distance <= nodeRadius;
        return inRadius;
    }

}
