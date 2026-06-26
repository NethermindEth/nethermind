// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.State.Snap;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.SnapSync
{
    public class SnapSyncDownloader(ILogManager? logManager) : ISyncDownloader<SnapSyncBatch>
    {
        private readonly ILogger Logger = (logManager ?? NullLogManager.Instance).GetClassLogger<SnapSyncDownloader>();

        public async Task Dispatch(PeerInfo peerInfo, SnapSyncBatch batch, CancellationToken cancellationToken)
        {
            ISyncPeer peer = peerInfo.SyncPeer;

            if (peer.TryGetSatelliteProtocol<ISnapSyncPeer>(Protocol.Snap, out ISnapSyncPeer? handler))
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
                    else if (batch.AccountsToRefreshRequest is { Paths.Count: > 0 })
                    {
                        // Refresh a single account via GetAccountRange so its storage root is verified against
                        // the state root. Use limit = path + 1 to avoid start == limit, which some peers treat as
                        // an empty range. (IncrementPath is a no-op only for the unreachable MaxValue path.)
                        AccountWithStorageStartingHash account = batch.AccountsToRefreshRequest.Paths[0];
                        ValueHash256 path = account.PathAndAccount.Path;
                        AccountRange range = new(batch.AccountsToRefreshRequest.RootHash, path, path.IncrementPath());
                        batch.AccountsToRefreshResponse = await handler.GetAccountRange(range, cancellationToken);
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
