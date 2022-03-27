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
using Nethermind.Specs;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization;


public interface ITotalDifficultyDependentMethods
{
    bool IsHeaderBetterThanPeer(BlockHeader? header, PeerInfo? peerInfo);

    bool IsPeerBetterThanHeader(BlockHeader? header, PeerInfo? peerInfo);

    bool ShouldUpdatePeer((UInt256 TotalDifficulty, long Number) newValues, ISyncPeer peerInfo);
}


public class TotalDifficultyDependentMethods : ITotalDifficultyDependentMethods
{
  //  private readonly ITotalDifficultyChecks _totalDifficultyChecks;
    private readonly ISyncProgressResolver _syncProgressResolver;
    private readonly ILogger _logger;
    
    public TotalDifficultyDependentMethods(
       // ITotalDifficultyChecks totalDifficultyChecks,
        ISyncProgressResolver syncProgressResolver,
        ILogManager logManager)
    {
     //   _totalDifficultyChecks = totalDifficultyChecks;
        _syncProgressResolver = syncProgressResolver;
        _logger = logManager.GetClassLogger();
    }
    
    public bool IsHeaderBetterThanPeer(BlockHeader? header, PeerInfo? peerInfo)
    {
        return Compare(header, peerInfo) > 0;
    }
    
    public bool IsPeerBetterThanHeader(BlockHeader? header, PeerInfo? peerInfo)
    {
        return Compare(header, peerInfo) < 0;
    }

    public bool ShouldUpdatePeer((UInt256 TotalDifficulty, long Number) newValues, ISyncPeer peerInfo)
    {
        return newValues.TotalDifficulty >= peerInfo.TotalDifficulty;
        // BlockHeader? parent = _blockTree.FindParentHeader(header, BlockTreeLookupOptions.None);
        //     if (parent != null && parent.TotalDifficulty != 0)
        // {
        //     UInt256 newTotalDifficulty = (parent.TotalDifficulty ?? UInt256.Zero) + header.Difficulty;
        //     if (newTotalDifficulty >= syncPeer.TotalDifficulty)
        //     {

    }

    public int Compare(BlockHeader? header, PeerInfo? peerInfo)
    {
        UInt256 headerDifficulty = header?.TotalDifficulty ?? 0;
        UInt256 peerDifficulty = peerInfo?.TotalDifficulty ?? 0;
        if (headerDifficulty > peerDifficulty) return 1;
        else if (headerDifficulty == peerDifficulty) return 0;
        else return -1;
    }
    
    // private (UInt256? maxPeerDifficulty, long? number) FindBestPeer()
    // {
    //     UInt256? maxPeerDifficulty = null;
    //     long? number = 0;
    //
    //     foreach (PeerInfo peer in _syncPeerPool.InitializedPeers)
    //     {
    //         UInt256 currentMax = maxPeerDifficulty ?? UInt256.Zero;
    //         if (peer.TotalDifficulty > currentMax || peer.TotalDifficulty == currentMax && peer.HeadNumber > number)
    //         {
    //             // we don't trust parity TotalDifficulty, so we are checking if we know the hash and get our total difficulty
    //             var realTotalDifficulty =
    //                 _syncProgressResolver.GetTotalDifficulty(peer.HeadHash) ?? peer.TotalDifficulty;
    //             if (realTotalDifficulty > currentMax ||
    //                 peer.TotalDifficulty == currentMax && peer.HeadNumber > number)
    //             {
    //                 maxPeerDifficulty = realTotalDifficulty;
    //                 number = peer.HeadNumber;
    //             }
    //         }
    //     }
    //
    //     return (maxPeerDifficulty, number);
    // }
    //
    // protected virtual bool AnyDesiredPeerKnown(Snapshot best)
    // {
    //     UInt256 localChainDifficulty = _syncProgressResolver.ChainDifficulty;
    //     bool anyDesiredPeerKnown = best.PeerDifficulty > localChainDifficulty
    //                                || best.PeerDifficulty == localChainDifficulty && best.PeerBlock > best.Header;
    //     if (anyDesiredPeerKnown)
    //     {
    //         if (_logger.IsTrace)
    //             _logger.Trace($"   Best peer [{best.PeerBlock},{best.PeerDifficulty}] " +
    //                           $"> local [{best.Header},{localChainDifficulty}]");
    //     }
    //
    //     return anyDesiredPeerKnown;
    // }

    // private void AnyPeers()
    // {
    //     bool anyPeers = peerBlock.Value > 0 &&
    //                     peerDifficulty.Value >= _syncProgressResolver.ChainDifficulty;
    //     newModes = anyPeers ? SyncMode.Full : SyncMode.Disconnected;
    // }
    
}
