// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Logging;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.FastBlocks
{
    public class HeadersSyncDownloader : ISyncDownloader<HeadersSyncBatch>
    {
        private ILogger Logger;

        public HeadersSyncDownloader(ILogManager logManager)
        {
            Logger = logManager.GetClassLogger();
        }

        async Task ISyncDownloader<HeadersSyncBatch>.Dispatch(
            PeerInfo peerInfo,
            HeadersSyncBatch batch,
            CancellationToken cancellationToken)
        {
            ISyncPeer peer = peerInfo.SyncPeer;
            batch.ResponseSourcePeer = peerInfo;
            batch.MarkSent();

            try
            {
                batch.Response = await peer.GetBlockHeaders(batch.StartNumber, batch.RequestSize, 0, cancellationToken);
            }
            catch (TimeoutException)
            {
                if (Logger.IsDebug) Logger.Debug($"{batch} - request block header timeout {batch.RequestTime:F2}");
                return;
            }
            if (batch.RequestTime > 1000)
            {
                if (Logger.IsDebug) Logger.Debug($"{batch} - peer is slow {batch.RequestTime:F2}");
            }
        }
    }
}
