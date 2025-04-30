// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.Blocks
{
    public class FullSyncFeed(IForwardSyncController forwardSyncController) : ActivatedSyncFeed<BlocksRequest?>
    {
        protected override SyncMode ActivationSyncModes { get; } = SyncMode.Full;

        private static DownloaderOptions BuildOptions() => DownloaderOptions.Process;

        // ReSharper disable once RedundantTypeArgumentsOfMethod
        public override Task<BlocksRequest?> PrepareRequest(CancellationToken token = default)
        {
            return forwardSyncController.PrepareRequest(BuildOptions(), 0, token);
        }

        public override SyncResponseHandlingResult HandleResponse(BlocksRequest? response, PeerInfo peer = null)
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
