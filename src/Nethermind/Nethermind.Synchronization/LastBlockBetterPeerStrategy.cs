// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.Synchronization;

public class LastBlockBetterPeerStrategy : IBetterPeerStrategy
{
    private readonly ILogger _logger;

    public LastBlockBetterPeerStrategy(ILogManager logManager)
    {
        _logger = logManager.GetClassLogger();
    }

    public int Compare(in (UInt256 TotalDifficulty, long Number) valueX, in (UInt256 TotalDifficulty, long Number) valueY) =>
        valueX.Number.CompareTo(valueY.Number);

    public bool IsBetterThanLocalChain(in (UInt256 TotalDifficulty, long Number) bestPeerInfo, in (UInt256 TotalDifficulty, long Number) bestBlock) =>
        Compare(bestPeerInfo, bestBlock) > 0;

    public bool IsDesiredPeer(in (UInt256 TotalDifficulty, long Number) bestPeerInfo, in (UInt256 TotalDifficulty, long Number) bestHeader)
    {
        bool desiredPeerKnown = IsBetterThanLocalChain(bestPeerInfo, bestHeader);
        if (desiredPeerKnown && _logger.IsTrace) _logger.Trace($"   Best peer [{bestPeerInfo.Number},{bestPeerInfo.TotalDifficulty}] > local [{bestHeader}, {bestHeader.TotalDifficulty}]");
        return desiredPeerKnown;
    }
}
