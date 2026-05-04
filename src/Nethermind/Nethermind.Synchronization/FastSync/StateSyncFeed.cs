// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Logging;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.FastSync
{
    public class StateSyncFeed(TreeSync treeSync, ILogManager logManager) : ISimpleSyncFeed<StateSyncBatch>
    {
        private readonly ILogger _logger = logManager.GetClassLogger<StateSyncFeed>();

        public async Task<StateSyncBatch?> PrepareRequest(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (treeSync.IsSyncRoundFinished()) return null;

                    StateSyncBatch? batch = await treeSync.PrepareRequest();
                    if (batch is not null) return batch;
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
                catch (Exception e)
                {
                    _logger.Error("Error when preparing a state sync batch", e);
                }

                try { await Task.Delay(50, token); }
                catch (OperationCanceledException) { return null; }
            }

            return null;
        }

        public SyncResponseHandlingResult HandleResponse(StateSyncBatch batch, PeerInfo? peer = null)
        {
            using StateSyncBatch b = batch;
            return treeSync.HandleResponse(b, peer);
        }
    }
}
