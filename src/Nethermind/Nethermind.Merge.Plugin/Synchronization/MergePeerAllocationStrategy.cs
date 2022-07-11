//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
//
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
//

using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Stats;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;

namespace Nethermind.Merge.Plugin.Synchronization;

public class MergePeerAllocationStrategy : IPeerAllocationStrategy
{
    private readonly IPeerAllocationStrategy _preMergeAllocationStrategy;
    private readonly IPeerAllocationStrategy _postMergeAllocationStrategy;
    private readonly IPoSSwitcher _poSSwitcher;
    private readonly ILogger _logger;
    public bool CanBeReplaced => true;

    public MergePeerAllocationStrategy(
        IPeerAllocationStrategy preMergeAllocationStrategy,
        IPeerAllocationStrategy postMergeAllocationStrategy,
        IPoSSwitcher poSSwitcher,
        ILogManager logManager)
    {
        _preMergeAllocationStrategy = preMergeAllocationStrategy;
        _postMergeAllocationStrategy = postMergeAllocationStrategy;
        _poSSwitcher = poSSwitcher;
        _logger = logManager.GetClassLogger();
    }

    // ReSharper disable PossibleMultipleEnumeration
    public PeerInfo? Allocate(PeerInfo? currentPeer, IEnumerable<PeerInfo> peers, INodeStatsManager nodeStatsManager, IBlockTree blockTree)
    {
        UInt256? terminalTotalDifficulty = _poSSwitcher.TerminalTotalDifficulty;
        bool isPostMerge = IsPostMerge;
        IEnumerable<PeerInfo> peerInfos = peers as PeerInfo[] ?? peers.ToArray();
        IEnumerable<PeerInfo> postTTDPeers = peerInfos.Where(p => p.TotalDifficulty >= terminalTotalDifficulty);
        bool anyPostMergePeers = postTTDPeers.Any();
        if (_logger.IsTrace) _logger.Trace($"{nameof(MergePeerAllocationStrategy)}: IsPostMerge: {isPostMerge} AnyPostMergePeers: {anyPostMergePeers}, CurrentPeer: {currentPeer} Peers: {string.Join(",", peerInfos)}");
        PeerInfo? peerInfo = isPostMerge || anyPostMergePeers
            ? _postMergeAllocationStrategy.Allocate(currentPeer, postTTDPeers, nodeStatsManager, blockTree)
            : _preMergeAllocationStrategy.Allocate(currentPeer, peerInfos, nodeStatsManager, blockTree);

        if (_logger.IsTrace) _logger.Trace($"{nameof(MergePeerAllocationStrategy)}: Result of peer allocation {peerInfo}");
        return peerInfo;
    }
    // ReSharper restore PossibleMultipleEnumeration

    private bool IsPostMerge => _poSSwitcher.HasEverReachedTerminalBlock() || _poSSwitcher.TransitionFinished;

    public override string ToString() => $"{nameof(MergePeerAllocationStrategy)} ({(IsPostMerge ? _postMergeAllocationStrategy.ToString() : _preMergeAllocationStrategy.ToString())})";
}
