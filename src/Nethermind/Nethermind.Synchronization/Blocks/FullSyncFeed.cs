// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Logging;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.Blocks
{
    public class FullSyncFeed : ActivatedSyncFeed<BlocksRequest?>
    {
        private readonly BlocksRequest _blocksRequest;

        public FullSyncFeed(ISyncModeSelector syncModeSelector, ILogManager logManager)
            : base(syncModeSelector)
        {
            _blocksRequest = new BlocksRequest(BuildOptions());
        }

        protected override SyncMode ActivationSyncModes => SyncMode.Full;

        private static DownloaderOptions BuildOptions() => DownloaderOptions.WithBodies | DownloaderOptions.Process;

        // ReSharper disable once RedundantTypeArgumentsOfMethod
        public override ValueTask<BlocksRequest?> PrepareRequest(CancellationToken token = default) => new(_blocksRequest);

        public override ValueTask<SyncResponseHandlingResult> HandleResponse(BlocksRequest? response, PeerInfo peer = null)
        {
            FallAsleep();
            return new(SyncResponseHandlingResult.OK);
        }

        public override bool IsMultiFeed => false;

        public override AllocationContexts Contexts => AllocationContexts.Blocks;
    }
}
