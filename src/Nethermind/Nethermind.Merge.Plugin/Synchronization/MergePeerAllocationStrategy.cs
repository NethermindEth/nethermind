// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

    public PeerInfo? Allocate(PeerInfo? currentPeer, IEnumerable<PeerInfo> peers, INodeStatsManager nodeStatsManager, IBlockTree blockTree)
    {
        UInt256? terminalTotalDifficulty = _poSSwitcher.TerminalTotalDifficulty;
        bool isPostMerge = IsPostMerge;
        IEnumerable<PeerInfo> peerInfos = peers as PeerInfo[] ?? peers.ToArray();
        IEnumerable<PeerInfo> postTTDPeers = peerInfos.Where(p => p.TotalDifficulty >= terminalTotalDifficulty);
        bool anyPostMergePeers = postTTDPeers.Any();
        if (_logger.IsTrace) _logger.Trace($"{nameof(MergePeerAllocationStrategy)}: IsPostMerge: {isPostMerge} AnyPostMergePeers: {anyPostMergePeers}, CurrentPeer: {currentPeer} Peers: {string.Join(",", peerInfos)}");
        PeerInfo? peerInfo = isPostMerge || anyPostMergePeers
            ? _postMergeAllocationStrategy.Allocate(currentPeer, peerInfos, nodeStatsManager, blockTree) // A hive test requires syncing to peer with TD < TTD, so we still need all peers.
            : _preMergeAllocationStrategy.Allocate(currentPeer, peerInfos, nodeStatsManager, blockTree);

        if (_logger.IsTrace) _logger.Trace($"MergePeerAllocationStrategy: Result of peer allocation {peerInfo}");
        return peerInfo;
    }

    private bool IsPostMerge => _poSSwitcher.HasEverReachedTerminalBlock() || _poSSwitcher.TransitionFinished;

    public override string ToString() => $"{nameof(MergePeerAllocationStrategy)} ({(IsPostMerge ? _postMergeAllocationStrategy.ToString() : _preMergeAllocationStrategy.ToString())})";
}
