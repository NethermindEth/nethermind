// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.Blocks
{
    public class FastSyncFeed : ActivatedSyncFeed<BlocksRequest>
    {
        private readonly ISyncConfig _syncConfig;
        private readonly BlocksRequest _blocksRequest;

        public FastSyncFeed(ISyncConfig syncConfig)
        {
            _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));
            _blocksRequest = new BlocksRequest(BuildOptions(), syncConfig.StateMinDistanceFromHead);
        }

        protected override SyncMode ActivationSyncModes { get; } = SyncMode.FastSync;

        private DownloaderOptions BuildOptions()
        {
            return DownloaderOptions.Insert | DownloaderOptions.WithReceipts;
        }

        public override Task<BlocksRequest> PrepareRequest(CancellationToken token = default) => Task.FromResult(_blocksRequest);

        public override SyncResponseHandlingResult HandleResponse(BlocksRequest response, PeerInfo peer = null)
        {
            FallAsleep();
            return SyncResponseHandlingResult.OK;
        }

        public override bool IsMultiFeed => false;

        public override AllocationContexts Contexts => AllocationContexts.Blocks;
        public override bool IsFinished => false; // Check MultiSyncModeSelector
    }
}
