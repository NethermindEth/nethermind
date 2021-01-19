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
using Nethermind.Logging;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Consensus.AuRa
{
    public class AuRaMultiSyncModeSelector : MultiSyncModeSelector
    {
        public AuRaMultiSyncModeSelector(ISyncProgressResolver syncProgressResolver, ISyncPeerPool syncPeerPool, ISyncConfig syncConfig, ILogManager logManager) 
            : base(syncProgressResolver, syncPeerPool, syncConfig, logManager)
        {
        }
        
        protected override bool AnyDesiredPeerKnown(Snapshot best)
        {
            if (base.AnyDesiredPeerKnown(best))
            {
                // We really, really don't trust parity when its saying it has same block level with higher difficulty in AuRa, its lying most of the times in AuRa.
                // This is because if its different block we already imported, but on same level, it will have lower difficulty (AuRa specific).
                // If we imported the previous one than we probably shouldn't import this one.
                bool ignoreParitySameLevel = best.PeerBlock == best.Header;

                // We can ignore reorg for one round, if we accepted previous block fine, this reorg is malicious
                if (ignoreParitySameLevel)
                {
                    if (_logger.IsDebug) _logger.Debug($"Ignoring best peer [{best.PeerBlock},{best.PeerDifficulty}], possible Parity/OpenEthereum outlier.");
                    return false;
                }

                return true;
            }

            return false;
        }
    }
}
