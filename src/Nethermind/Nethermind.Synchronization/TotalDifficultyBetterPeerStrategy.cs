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
