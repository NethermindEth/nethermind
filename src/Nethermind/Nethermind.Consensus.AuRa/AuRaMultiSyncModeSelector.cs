//  Copyright (c) 2018 Demerzel Solutions Limited
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
using Nethermind.Logging;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Consensus.AuRa
{
    public class AuRaMultiSyncModeSelector : MultiSyncModeSelector
    {
        public AuRaMultiSyncModeSelector(ISyncProgressResolver syncProgressResolver, ISyncPeerPool syncPeerPool, ISyncConfig syncConfig, ILogManager logManager, bool withAutoUpdates = true) 
            : base(syncProgressResolver, syncPeerPool, syncConfig, logManager, withAutoUpdates)
        {
        }
        
        protected override bool AnyDesiredPeerKnown(Snapshot best)
        {
            if (base.AnyDesiredPeerKnown(best))
            {
                if (best.Peer.PeerClientType == PeerClientType.Parity || best.Peer.PeerClientType == PeerClientType.OpenEthereum)
                {
                    // we really, really don't trust parity when its saying it has same block level with higher difficulty in AuRa, its lying most of the times in AuRa
                    bool ignoreParitySameLevel = best.Peer.HeadNumber == best.Header;
                    
                    if (ignoreParitySameLevel)
                    {
                        if (_logger.IsInfo) _logger.Info($"Ignoring best peer [{best.Peer.HeadNumber},{best.Peer.TotalDifficulty}], possible Parity/OpenEthereum outlier.");
                        return false;
                    }
                }

                return true;
            }
            return false;
        }
    }
}
