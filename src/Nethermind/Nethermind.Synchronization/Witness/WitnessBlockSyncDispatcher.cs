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

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Logging;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.Witness
{
    public class WitnessBlockSyncDispatcher : SyncDispatcher<WitnessBlockSyncBatch>
    {
        public WitnessBlockSyncDispatcher(ISyncFeed<WitnessBlockSyncBatch> syncFeed, ISyncPeerPool syncPeerPool, IPeerAllocationStrategyFactory<WitnessBlockSyncBatch> peerAllocationStrategy, ILogManager logManager) 
            : base(syncFeed, syncPeerPool, peerAllocationStrategy, logManager)
        {
        }

        protected override async Task Dispatch(PeerInfo peerInfo, WitnessBlockSyncBatch request, CancellationToken cancellationToken)
        {
            ISyncPeer peer = peerInfo.SyncPeer;
            if (peer.TryGetSatelliteProtocol<IWitnessPeer>("wit", out var witnessProtocolHandler) && witnessProtocolHandler != null)
            {
                var getNodeDataTask = witnessProtocolHandler.GetBlockWitnessHashes(request.BlockHash, cancellationToken);
                await getNodeDataTask.ContinueWith(
                    (t, state) =>
                    {
                        if (t.IsFaulted)
                        {
                            if(Logger.IsTrace) Logger.Error("DEBUG/ERROR Error after dispatching the state sync request", t.Exception);
                        }
                    
                        WitnessBlockSyncBatch batchLocal = (WitnessBlockSyncBatch) state!;
                        if (t.IsCompletedSuccessfully)
                        {
                            batchLocal.Response = t.Result;
                        }
                    }, request, cancellationToken);
            }
            else
            {
                if(Logger.IsError) Logger.Error($"Couldn't get witness protocol from {peer.Node}.");
            }
        }

        protected override async Task<SyncPeerAllocation> Allocate(WitnessBlockSyncBatch request)
        {
            var allocate = await base.Allocate(request);
            return allocate;
        }
    }
}
