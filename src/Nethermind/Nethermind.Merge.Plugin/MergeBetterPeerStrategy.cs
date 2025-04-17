// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;
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
        if (_logger.IsTrace) _logger.Trace(
            $"IsDesiredPeer: " +
            $"_beaconPivot.PivotNumber: {_beaconPivot.PivotNumber}, " +
            $"bestPeerInfo.Number: {bestPeerInfo.Number}, " +
            $"bestPeerInfo.TotalDifficulty: {bestPeerInfo.TotalDifficulty}, " +
            $"bestHeader.TotalDifficulty: {bestHeader.TotalDifficulty}, " +
            $"_posSwitcher.TerminalTotalDifficulty: {_poSSwitcher.TerminalTotalDifficulty}, ");

        // Post-merge it depends on the beacon pivot.
        // Some hive test sync to a lower number and have peer without the beacon pivot, but it has
        // the pivot's parent. So we need to allow peer with the parent of the beacon pivot.
        if (_beaconPivot.BeaconPivotExists())
        {
            return bestPeerInfo.Number >= _beaconPivot.PivotNumber - 1;
        }

        if (ShouldApplyPreMergeLogic(bestPeerInfo.TotalDifficulty, bestHeader.TotalDifficulty))
        {
            return _preMergeBetterPeerStrategy.IsDesiredPeer(bestPeerInfo, bestHeader);
        }

        return false;
    }

    public bool IsLowerThanTerminalTotalDifficulty(UInt256 totalDifficulty) =>
        _poSSwitcher.TerminalTotalDifficulty is null || totalDifficulty < _poSSwitcher.TerminalTotalDifficulty;

    private bool ShouldApplyPreMergeLogic(UInt256 totalDifficultyX, UInt256 totalDifficultyY) =>
        IsLowerThanTerminalTotalDifficulty(totalDifficultyX) || IsLowerThanTerminalTotalDifficulty(totalDifficultyY);

}
