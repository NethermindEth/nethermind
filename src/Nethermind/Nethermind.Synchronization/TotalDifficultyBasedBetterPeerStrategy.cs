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
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.Synchronization;

/*
* The class provides an abstraction for checks connected with TotalDifficulty across Nethermind.Synchronization.csproj.
* This abstraction is needed for The Merge, because we need to rewrite most of the TotalDifficulty checks.
* Because of many formats, we had to implement many similar methods.
 * 
 */

public interface IBetterPeerStrategy
{
    int Compare(BlockHeader? header, ISyncPeer? peerInfo);

    int Compare((UInt256 TotalDifficulty, long Number) value, ISyncPeer? peerInfo);

    int Compare((UInt256 TotalDifficulty, long Number) valueX, (UInt256 TotalDifficulty, long Number) valueY);

    bool IsBetterThanLocalChain((UInt256 TotalDifficulty, long Number) bestPeerInfo);

    bool IsDesiredPeer((UInt256 TotalDifficulty, long Number) bestPeerInfo, long bestHeader);
    
    bool IsLowerThanTerminalTotalDifficulty(UInt256 totalDifficulty);
}


public class TotalDifficultyBasedBetterPeerStrategy : IBetterPeerStrategy
{
    private readonly ISyncProgressResolver _syncProgressResolver;
    private readonly ILogger _logger;
    
    public TotalDifficultyBasedBetterPeerStrategy(
        ISyncProgressResolver syncProgressResolver,
        ILogManager logManager)
    {
        _syncProgressResolver = syncProgressResolver;
        _logger = logManager.GetClassLogger();
    }
    
    public int Compare(BlockHeader? header, ISyncPeer? peerInfo)
    {
        UInt256 headerDifficulty = header?.TotalDifficulty ?? 0;
        UInt256 peerDifficulty = peerInfo?.TotalDifficulty ?? 0;
        return headerDifficulty.CompareTo(peerDifficulty);
    }

    public int Compare((UInt256 TotalDifficulty, long Number) value, ISyncPeer? peerInfo)
    {
        UInt256 peerDifficulty = peerInfo?.TotalDifficulty ?? 0;
        return value.TotalDifficulty.CompareTo(peerDifficulty);
    }
    
    public int Compare((UInt256 TotalDifficulty, long Number) valueX, (UInt256 TotalDifficulty, long Number) valueY)
    {
        return valueX.TotalDifficulty.CompareTo(valueY.TotalDifficulty);
    }
    
    public bool IsBetterThanLocalChain((UInt256 TotalDifficulty, long Number) bestPeerInfo)
    {
        UInt256 localChainDifficulty = _syncProgressResolver.ChainDifficulty;
        return bestPeerInfo.TotalDifficulty.CompareTo(localChainDifficulty) > 0;
    }

    public bool IsDesiredPeer((UInt256 TotalDifficulty, long Number) bestPeerInfo, long bestHeader)
    {
        UInt256 localChainDifficulty = _syncProgressResolver.ChainDifficulty;
        bool desiredPeerKnown = IsBetterThanLocalChain(bestPeerInfo)
                                || bestPeerInfo.TotalDifficulty == localChainDifficulty && bestPeerInfo.Number > bestHeader;
        if (desiredPeerKnown)
        {
            if (_logger.IsTrace)
                _logger.Trace($"   Best peer [{bestPeerInfo.Number},{bestPeerInfo.TotalDifficulty}] " +
                              $"> local [{bestHeader},{localChainDifficulty}]");
        }

        return desiredPeerKnown;
    }

    public bool IsLowerThanTerminalTotalDifficulty(UInt256 totalDifficulty) => true;
}
