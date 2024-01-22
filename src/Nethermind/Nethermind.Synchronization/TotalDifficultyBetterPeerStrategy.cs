// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.Synchronization;

public class TotalDifficultyBetterPeerStrategy : IBetterPeerStrategy
{
    private readonly ILogger _logger;

    public TotalDifficultyBetterPeerStrategy(ILogManager logManager)
    {
        _logger = logManager.GetClassLogger();
    }

    public int Compare(in (UInt256 TotalDifficulty, long Number) valueX, in (UInt256 TotalDifficulty, long Number) valueY) =>
        valueX.TotalDifficulty.CompareTo(valueY.TotalDifficulty);

    public bool IsBetterThanLocalChain(in (UInt256 TotalDifficulty, long Number) bestPeerInfo, in (UInt256 TotalDifficulty, long Number) bestBlock) =>
        Compare(bestPeerInfo, bestBlock) > 0;

    public bool IsDesiredPeer(in (UInt256 TotalDifficulty, long Number) bestPeerInfo, in (UInt256 TotalDifficulty, long Number) bestHeader)
    {
        bool desiredPeerKnown = IsBetterThanLocalChain(bestPeerInfo, bestHeader) || bestPeerInfo.TotalDifficulty == bestHeader.TotalDifficulty && bestPeerInfo.Number > bestHeader.Number;
        if (desiredPeerKnown && _logger.IsTrace) _logger.Trace($"   Best peer [{bestPeerInfo.Number},{bestPeerInfo.TotalDifficulty}] > local [{bestHeader}, {bestHeader.TotalDifficulty}]");
        return desiredPeerKnown;
    }
}
