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

using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Synchronization;

namespace Nethermind.Merge.Plugin;

public class MergeBetterPeerStrategy : IBetterPeerStrategy
{
    private readonly IBetterPeerStrategy _preMergeBetterPeerStrategy;
    private readonly IPoSSwitcher _poSSwitcher;
    private readonly IBeaconPivot _beaconPivot;
    private readonly ILogger _logger;

    public MergeBetterPeerStrategy(
        IBetterPeerStrategy preMergeBetterPeerStrategy,
        IPoSSwitcher poSSwitcher,
        IBeaconPivot beaconPivot,
        ILogManager logManager)
    {
        _preMergeBetterPeerStrategy = preMergeBetterPeerStrategy;
        _poSSwitcher = poSSwitcher;
        _beaconPivot = beaconPivot;
        _logger = logManager.GetClassLogger();
    }

    public int Compare(in (UInt256 TotalDifficulty, long Number) valueX, in (UInt256 TotalDifficulty, long Number) valueY) =>
        ShouldApplyPreMergeLogic(valueX.TotalDifficulty, valueY.TotalDifficulty)
            ? _preMergeBetterPeerStrategy.Compare(valueX, valueY)
            : valueX.Number.CompareTo(valueY.Number);

    public bool IsBetterThanLocalChain(in (UInt256 TotalDifficulty, long Number) bestPeerInfo, in (UInt256 TotalDifficulty, long Number) bestBlock)
    {
        if (_logger.IsTrace) _logger.Trace($"IsBetterThanLocalChain BestPeerInfo.TD: {bestPeerInfo.TotalDifficulty}, BestPeerInfo.Number: {bestPeerInfo.Number}, LocalChainDifficulty {bestBlock.TotalDifficulty} LocalChainBestFullBlock: {bestBlock.Number} TerminalTotalDifficulty {_poSSwitcher.TerminalTotalDifficulty}");
        return ShouldApplyPreMergeLogic(bestPeerInfo.TotalDifficulty, bestBlock.TotalDifficulty)
            ? _preMergeBetterPeerStrategy.IsBetterThanLocalChain(bestPeerInfo, bestBlock)
            : bestPeerInfo.Number > bestBlock.Number;
    }

    public bool IsDesiredPeer(in (UInt256 TotalDifficulty, long Number) bestPeerInfo, in (UInt256 TotalDifficulty, long Number) bestHeader)
    {
        bool isDesiredPeer = ShouldApplyPreMergeLogic(bestPeerInfo.TotalDifficulty, bestHeader.TotalDifficulty)
            ? _preMergeBetterPeerStrategy.IsDesiredPeer(bestPeerInfo, bestHeader)
            : _beaconPivot.BeaconPivotExists() && bestPeerInfo.Number >= _beaconPivot.PivotNumber - 1; // we need  to guarantee the peer can have all the block prior to beacon pivot
        if (_logger.IsTrace) _logger.Trace($"IsDesiredPeer {isDesiredPeer} BestPeerInfo.TD: {bestPeerInfo.TotalDifficulty}, BestPeerInfo.Number: {bestPeerInfo.Number}, LocalChainDifficulty {bestHeader.TotalDifficulty} LocalChainBestFullBlock: {bestHeader.Number} TerminalTotalDifficulty {_poSSwitcher.TerminalTotalDifficulty} BeaconPivotExists {_beaconPivot.BeaconPivotExists()} BeaconPivotNumber {_beaconPivot.PivotNumber}");
        return isDesiredPeer;
    }

    public bool IsLowerThanTerminalTotalDifficulty(UInt256 totalDifficulty) =>
        _poSSwitcher.TerminalTotalDifficulty is null || totalDifficulty < _poSSwitcher.TerminalTotalDifficulty;

    private bool ShouldApplyPreMergeLogic(UInt256 totalDifficultyX, UInt256 totalDifficultyY) =>
        IsLowerThanTerminalTotalDifficulty(totalDifficultyX) || IsLowerThanTerminalTotalDifficulty(totalDifficultyY);

}
