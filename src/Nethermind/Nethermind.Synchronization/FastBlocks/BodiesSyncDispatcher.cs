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
    public class BodiesSyncDispatcher : SyncDispatcher<BodiesSyncBatch>
    {
        public BodiesSyncDispatcher(
            ISyncFeed<BodiesSyncBatch> syncFeed,
            ISyncPeerPool syncPeerPool,
            IPeerAllocationStrategyFactory<BodiesSyncBatch> peerAllocationStrategy,
            ILogManager logManager)
            : base(syncFeed, syncPeerPool, peerAllocationStrategy, logManager)
        {
        }

        protected override async Task Dispatch(PeerInfo peerInfo, BodiesSyncBatch batch, CancellationToken cancellationToken)
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
                batch.Response = await peer.GetBlockBodies(hashes, cancellationToken);
            }
            catch (TimeoutException)
            {
                if (Logger.IsDebug) Logger.Debug($"{batch} - Get block bodies timeout {batch.RequestTime:F2}");
                return;
            }
            if (batch.RequestTime > 1000)
            {
                if (Logger.IsDebug) Logger.Debug($"{batch} - peer is slow {batch.RequestTime:F2}");
            }
        }
    }
}
