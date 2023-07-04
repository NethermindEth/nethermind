// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Synchronization.Peers.AllocationStrategies;

public class ClientTypeStrategy : IPeerAllocationStrategy
{
    private readonly IPeerAllocationStrategy _strategy;
    private readonly bool _allowOtherIfNone;
    private readonly HashSet<NodeClientType> _supportedClientTypes;

    public ClientTypeStrategy(IPeerAllocationStrategy strategy, bool allowOtherIfNone, params NodeClientType[] supportedClientTypes)
        : this(strategy, allowOtherIfNone, (IEnumerable<NodeClientType>)supportedClientTypes)
    {
    }

    public ClientTypeStrategy(IPeerAllocationStrategy strategy, bool allowOtherIfNone, IEnumerable<NodeClientType> supportedClientTypes)
    {
        _strategy = strategy;
        _allowOtherIfNone = allowOtherIfNone;
        _supportedClientTypes = new HashSet<NodeClientType>(supportedClientTypes);
    }

    public bool CanBeReplaced => _strategy.CanBeReplaced;

    public PeerInfo? Allocate(PeerInfo? currentPeer, IEnumerable<PeerInfo> peers, INodeStatsManager nodeStatsManager, IBlockTree blockTree)
    {
        IEnumerable<PeerInfo> originalPeers = peers;
        peers = peers.Where(p => _supportedClientTypes.Contains(p.PeerClientType));

        if (_allowOtherIfNone)
        {
            if (!peers.Any())
            {
                peers = originalPeers;
            }
        }
        return _strategy.Allocate(currentPeer, peers, nodeStatsManager, blockTree);
    }
}
