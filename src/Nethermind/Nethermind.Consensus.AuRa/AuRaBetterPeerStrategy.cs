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
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Synchronization;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Consensus.AuRa;

public class AuRaBetterPeerStrategy : IBetterPeersStrategy
{
    private readonly IBetterPeersStrategy _betterPeersStrategy;
    private readonly ILogger _logger;

    public AuRaBetterPeerStrategy(IBetterPeersStrategy betterPeersStrategy, ILogManager logManager)
    {
        _betterPeersStrategy = betterPeersStrategy;
        _logger = logManager.GetClassLogger();
    }

    public bool IsHeaderBetterThanPeer(BlockHeader? header, PeerInfo? peerInfo) =>
        _betterPeersStrategy.IsHeaderBetterThanPeer(header, peerInfo);

    public bool IsPeerBetterThanHeader(BlockHeader? header, PeerInfo? peerInfo) =>
        _betterPeersStrategy.IsPeerBetterThanHeader(header, peerInfo);

    public bool IsNotWorseThanPeer((UInt256 TotalDifficulty, long Number) newValues, ISyncPeer peerInfo) =>
        _betterPeersStrategy.IsNotWorseThanPeer(newValues, peerInfo);

    public (UInt256? maxPeerDifficulty, long? number) FindBestPeer(IEnumerable<PeerInfo> initializedPeers) =>
        _betterPeersStrategy.FindBestPeer(initializedPeers);

    public bool IsBetterThanLocalChain((UInt256 TotalDifficulty, long Number) bestPeerInfo) =>
        _betterPeersStrategy.IsBetterThanLocalChain(bestPeerInfo);

    public bool IsBetterPeer((UInt256 TotalDifficulty, long Number) bestPeerInfo, long bestHeader)
    {
        if (_betterPeersStrategy.IsBetterPeer(bestPeerInfo, bestHeader))
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
}
