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

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Logging;
using Nethermind.State.Snap;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.SnapSync
{
    public class SnapSyncDispatcher : SyncDispatcher<AccountsSyncBatch>
    {
        private readonly IBlockTree _blockTree;

        public SnapSyncDispatcher(ISyncFeed<AccountsSyncBatch>? syncFeed, ISyncPeerPool? syncPeerPool, IPeerAllocationStrategyFactory<AccountsSyncBatch>? peerAllocationStrategy, IBlockTree blockTree, ILogManager? logManager) 
            : base(syncFeed, syncPeerPool, peerAllocationStrategy, logManager)
        {
            _blockTree = blockTree;
        }

        protected override async Task Dispatch(PeerInfo peerInfo, AccountsSyncBatch batch, CancellationToken cancellationToken)
        {
            Logger.Info("DISPATCH");

            ISyncPeer peer = peerInfo.SyncPeer;

            //TODO: replace with a constant "snap"
            if (peer.TryGetSatelliteProtocol<ISnapSyncPeer>("snap", out var handler))
            {
                Task<AccountsAndProofs> task = handler.GetAccountRange(batch.Request, cancellationToken);

                await task.ContinueWith(
                    (t, state) =>
                    {
                        if (t.IsFaulted)
                        {
                            if (Logger.IsTrace)
                                Logger.Error("DEBUG/ERROR Error after dispatching the snap sync request", t.Exception);
                        }

                        AccountsSyncBatch batchLocal = (AccountsSyncBatch)state!;
                        if (t.IsCompletedSuccessfully)
                        {
                            batchLocal.Response = t.Result;
                        }
                    }, batch);
            }

            await Task.CompletedTask;
        }
    }
}
