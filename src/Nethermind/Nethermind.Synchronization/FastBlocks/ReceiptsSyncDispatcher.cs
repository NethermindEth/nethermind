// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.FastBlocks
{
    public class ReceiptsSyncDispatcher : SyncDispatcher<ReceiptsSyncBatch>
    {
        public ReceiptsSyncDispatcher(
            ISyncFeed<ReceiptsSyncBatch> syncFeed,
            ISyncPeerPool syncPeerPool,
            IPeerAllocationStrategyFactory<ReceiptsSyncBatch> peerAllocationStrategy,
            ILogManager logManager)
            : base(syncFeed, syncPeerPool, peerAllocationStrategy, logManager)
        {
        }

        protected override async Task Dispatch(PeerInfo peerInfo, ReceiptsSyncBatch batch, CancellationToken cancellationToken)
        {
            ISyncPeer peer = peerInfo.SyncPeer;
            batch.ResponseSourcePeer = peerInfo;
            batch.MarkSent();

            Keccak[]? hashes = batch.Infos.Where(i => i is not null).Select(i => i!.BlockHash).ToArray();
            if (hashes.Length == 0)
            {
                if (Logger.IsDebug) Logger.Debug($"{batch} - attempted send a request with no hash.");
                return;
            }

            try
            {
                batch.Response = await peer.GetReceipts(hashes, cancellationToken);
            }
            catch (TimeoutException)
            {
                if (Logger.IsDebug) Logger.Debug($"{batch} - request receipts timeout {batch.RequestTime:F2}");
                return;
            }

            if (batch.RequestTime > 1000)
            {
                if (Logger.IsDebug) Logger.Debug($"{batch} - peer is slow {batch.RequestTime:F2}");
            }
        }
    }
}
