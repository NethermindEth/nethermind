// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Logging;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.VerkleSync;

public class VerkleSyncDownloader: ISyncDownloader<VerkleSyncBatch>
{
    private ILogger Logger;

    public VerkleSyncDownloader(ILogManager? logManager)
    {
        Logger = logManager.GetClassLogger();
    }

    public async Task Dispatch(PeerInfo peerInfo, VerkleSyncBatch batch,
        CancellationToken cancellationToken)
    {
        ISyncPeer peer = peerInfo.SyncPeer;

        //TODO: replace with a constant "snap"
        if (peer.TryGetSatelliteProtocol<IVerkleSyncPeer>("verkle", out IVerkleSyncPeer? handler))
        {
            try
            {
                if (batch.SubTreeRangeRequest is not null)
                {
                    batch.SubTreeRangeResponse = await handler.GetSubTreeRange(batch.SubTreeRangeRequest, cancellationToken);
                }
                else if (batch.LeafToRefreshRequest is not null)
                {
                    batch.LeafToRefreshResponse = await handler.GetLeafNodes(batch.LeafToRefreshRequest, cancellationToken);
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
