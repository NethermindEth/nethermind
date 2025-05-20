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
    public class FastSyncFeed(IForwardSyncController forwardSyncController, ISyncConfig syncConfig)
        : ActivatedSyncFeed<BlocksRequest>
    {
        protected override SyncMode ActivationSyncModes { get; } = SyncMode.FastSync;

        private DownloaderOptions BuildOptions()
        {
            return DownloaderOptions.Insert | DownloaderOptions.WithReceipts;
        }

        public override Task<BlocksRequest> PrepareRequest(CancellationToken token = default)
        {
            return forwardSyncController.PrepareRequest(BuildOptions(), syncConfig.StateMinDistanceFromHead, token);
        }

        public override SyncResponseHandlingResult HandleResponse(BlocksRequest response, PeerInfo peer = null)
        {
            return forwardSyncController.HandleResponse(response, peer);
        }

        public override bool IsMultiFeed => true;

        public override AllocationContexts Contexts => AllocationContexts.Blocks;
        public override bool IsFinished => false; // Check MultiSyncModeSelector

        public override void FallAsleep()
        {
            base.FallAsleep();
            forwardSyncController.PruneDownloadBuffer();
        }
    }
}
