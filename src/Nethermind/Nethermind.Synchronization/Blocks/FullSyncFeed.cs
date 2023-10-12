// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.Blocks
{
    public class FullSyncFeed : ActivatedSyncFeed<BlocksRequest?>
    {
        private readonly BlocksRequest _blocksRequest = new(BuildOptions());

        protected override SyncMode ActivationSyncModes { get; } = SyncMode.Full;

        private static DownloaderOptions BuildOptions() => DownloaderOptions.WithBodies | DownloaderOptions.Process;

        // ReSharper disable once RedundantTypeArgumentsOfMethod
        public override Task<BlocksRequest?> PrepareRequest(CancellationToken token = default) => Task.FromResult<BlocksRequest?>(_blocksRequest);

        public override SyncResponseHandlingResult HandleResponse(BlocksRequest? response, PeerInfo peer = null)
        {
            FallAsleep();
            return SyncResponseHandlingResult.OK;
        }

        public override bool IsMultiFeed => false;

        public override AllocationContexts Contexts => AllocationContexts.Blocks;
    }
}
