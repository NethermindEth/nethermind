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

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Logging;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.SnapSync
{
    public class AccountsSyncDispatcher : SyncDispatcher<SnapSyncBatch>
    {
        public AccountsSyncDispatcher(ISyncFeed<SnapSyncBatch>? syncFeed, ISyncPeerPool? syncPeerPool, IPeerAllocationStrategyFactory<SnapSyncBatch>? peerAllocationStrategy, ILogManager? logManager) 
            : base(syncFeed, syncPeerPool, peerAllocationStrategy, logManager)
        {
        }

        protected override Task Dispatch(PeerInfo peerInfo, SnapSyncBatch batch, CancellationToken cancellationToken)
        {
            ISyncPeer peer = peerInfo.SyncPeer;
            // var getNodeDataTask = peer.GetAccountRange(batch.RequestedNodes.Select(n => n.Hash).ToArray(), cancellationToken);
        //     await getNodeDataTask.ContinueWith(
        //         (t, state) =>
        //         {
        //             if (t.IsFaulted)
        //             {
        //                 if(Logger.IsTrace) Logger.Error("DEBUG/ERROR Error after dispatching the state sync request", t.Exception);
        //             }
        //             
        //             StateSyncBatch batchLocal = (StateSyncBatch) state!;
        //             if (t.IsCompletedSuccessfully)
        //             {
        //                 batchLocal.Responses = t.Result;
        //             }
        //         }, request);

        return Task.CompletedTask;
        }
    }
}
