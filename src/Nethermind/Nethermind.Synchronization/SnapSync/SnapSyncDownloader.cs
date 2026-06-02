// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.SnapSync
{
    public class SnapSyncDownloader(ILogManager? logManager) : ISyncDownloader<SnapSyncBatch>
    {
        private readonly ILogger Logger = logManager.GetClassLogger<SnapSyncDownloader>();

        public async Task Dispatch(PeerInfo peerInfo, SnapSyncBatch batch, CancellationToken cancellationToken)
        {
            ISyncPeer peer = peerInfo.SyncPeer;

            if (peer.TryGetSatelliteProtocol<ISnapSyncPeer>(Protocol.Snap, out ISnapSyncPeer handler))
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
                catch (OperationCanceledException)
                {
                    if (Logger.IsDebug) Logger.Debug($"Snap sync request cancelled. Request: {batch}");
                }
                catch (Exception e)
                {
                    Logger.DebugError($"Error after dispatching the snap sync request. Request: {batch}", e);
                }
            }
        }
    }
}
