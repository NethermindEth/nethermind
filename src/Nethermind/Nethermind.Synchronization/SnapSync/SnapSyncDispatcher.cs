// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
    public class SnapSyncDispatcher : SyncDispatcher<SnapSyncBatch>
    {
        public SnapSyncDispatcher(
            int maxNumberOfProcessingThread,
            ISyncFeed<SnapSyncBatch>? syncFeed,
            ISyncPeerPool? syncPeerPool,
            IPeerAllocationStrategyFactory<SnapSyncBatch>? peerAllocationStrategy,
            ILogManager? logManager)
            : base(maxNumberOfProcessingThread, syncFeed, syncPeerPool, peerAllocationStrategy, logManager)
        {
        }

        protected override async Task Dispatch(PeerInfo peerInfo, SnapSyncBatch batch, CancellationToken cancellationToken)
        {
            ISyncPeer peer = peerInfo.SyncPeer;

            //TODO: replace with a constant "snap"
            if (peer.TryGetSatelliteProtocol<ISnapSyncPeer>("snap", out var handler))
            {
                try
                {
                    if (batch.AccountRangeRequest is not null)
                    {
                        batch.AccountRangeResponse = await handler.GetAccountRange(batch.AccountRangeRequest, cancellationToken);
                    }
                    else if (batch.StorageRangeRequest is not null)
                    {
                        batch.StorageRangeResponse = await handler.GetStorageRange(batch.StorageRangeRequest, cancellationToken);
                    }
                    else if (batch.CodesRequest is not null)
                    {
                        batch.CodesResponse = await handler.GetByteCodes(batch.CodesRequest, cancellationToken);
                    }
                    else if (batch.AccountsToRefreshRequest is not null)
                    {
                        batch.AccountsToRefreshResponse = await handler.GetTrieNodes(batch.AccountsToRefreshRequest, cancellationToken);
                    }
                }
                catch (Exception e)
                {
                    if (Logger.IsDebug)
                        Logger.Error($"DEBUG/ERROR Error after dispatching the snap sync request. Request: {batch}", e);
                }
            }

            await Task.CompletedTask;
        }
    }
}
