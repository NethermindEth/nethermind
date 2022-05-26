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
using Nethermind.Synchronization;

namespace Nethermind.Consensus.AuRa;

public class AuRaBetterPeerStrategy : IBetterPeerStrategy
{
    private readonly IBetterPeerStrategy _betterPeerStrategy;
    private readonly ILogger _logger;

    public AuRaBetterPeerStrategy(IBetterPeerStrategy betterPeerStrategy, ILogManager logManager)
    {
        _betterPeerStrategy = betterPeerStrategy;
        _logger = logManager.GetClassLogger();
    }

    public int Compare(BlockHeader? header, ISyncPeer? peerInfo)
        => _betterPeerStrategy.Compare(header, peerInfo);

    public int Compare((UInt256 TotalDifficulty, long Number) value, ISyncPeer? peerInfo)
        => _betterPeerStrategy.Compare(value, peerInfo);

    public int Compare((UInt256 TotalDifficulty, long Number) valueX, (UInt256 TotalDifficulty, long Number) valueY)
        => _betterPeerStrategy.Compare(valueX, valueY);

    public bool IsBetterThanLocalChain((UInt256 TotalDifficulty, long Number) bestPeerInfo) =>
        _betterPeerStrategy.IsBetterThanLocalChain(bestPeerInfo);

    public bool IsDesiredPeer((UInt256 TotalDifficulty, long Number) bestPeerInfo, long bestHeader)
    {
        if (_betterPeerStrategy.IsDesiredPeer(bestPeerInfo, bestHeader))
        {
            // We really, really don't trust parity when its saying it has same block level with higher difficulty in AuRa, its lying most of the times in AuRa.
            // This is because if its different block we already imported, but on same level, it will have lower difficulty (AuRa specific).
            // If we imported the previous one than we probably shouldn't import this one.
            bool ignoreParitySameLevel = bestPeerInfo.Number == bestHeader;

            // We can ignore reorg for one round, if we accepted previous block fine, this reorg is malicious
            if (ignoreParitySameLevel)
            {
                if (_logger.IsDebug) _logger.Debug($"Ignoring best peer [{bestPeerInfo.Number},{bestPeerInfo.TotalDifficulty}], possible Parity/OpenEthereum outlier.");
                return false;
            }

            return true;
        }

        return false;
        
    }

    public bool IsLowerThanTerminalTotalDifficulty(UInt256 totalDifficulty) =>
        _betterPeerStrategy.IsLowerThanTerminalTotalDifficulty(totalDifficulty);
}
