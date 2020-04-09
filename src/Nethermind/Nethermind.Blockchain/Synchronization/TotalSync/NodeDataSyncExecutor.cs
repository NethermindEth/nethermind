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

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization.FastSync;
using Nethermind.Logging;

namespace Nethermind.Blockchain.Synchronization.TotalSync
{
    public class NodeDataSyncExecutor : SyncExecutor<StateSyncBatch>
    {
        public NodeDataSyncExecutor(ISyncFeed<StateSyncBatch> syncFeed, IEthSyncPeerPool syncPeerPool, IPeerSelectionStrategyFactory<StateSyncBatch> peerSelectionStrategy, ILogManager logManager)
            : base(syncFeed, syncPeerPool, peerSelectionStrategy, logManager)
        {
        }

        protected override async Task Execute(PeerInfo peerInfo, StateSyncBatch request, CancellationToken cancellationToken)
        {
            ISyncPeer peer = peerInfo.SyncPeer;
            var getNodeDataTask = peer.GetNodeData(request.RequestedNodes.Select(n => n.Hash).ToArray(), cancellationToken);
            await getNodeDataTask.ContinueWith(
                (t, state) =>
                {
                    StateSyncBatch batchLocal = (StateSyncBatch) state;
                    if (t.IsCompletedSuccessfully)
                    {
                        batchLocal.Responses = t.Result;
                    }
                }, request);
        }
    }
}