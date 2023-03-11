// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Logging;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.Blocks
{
    public class FastSyncFeed : ActivatedSyncFeed<BlocksRequest>
    {
        private readonly ISyncConfig _syncConfig;
        private readonly BlocksRequest _blocksRequest;

        public FastSyncFeed(ISyncModeSelector syncModeSelector, ISyncConfig syncConfig, ILogManager logManager)
            : base(syncModeSelector)
        {
            _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));
            _blocksRequest = new BlocksRequest(BuildOptions(), MultiSyncModeSelector.FastSyncLag);
        }

        protected override SyncMode ActivationSyncModes { get; } = SyncMode.FastSync;

        private DownloaderOptions BuildOptions()
        {
            DownloaderOptions options = DownloaderOptions.MoveToMain;
            if (_syncConfig.DownloadReceiptsInFastSync)
            {
                options |= DownloaderOptions.WithReceipts;
            }

            if (_syncConfig.DownloadBodiesInFastSync)
            {
                options |= DownloaderOptions.WithBodies;
            }

            return options;
        }

        public override BlocksRequest PrepareRequest(CancellationToken token = default) => _blocksRequest;

        public override ValueTask<SyncResponseHandlingResult> HandleResponse(BlocksRequest response, PeerInfo peer = null)
        {
            FallAsleep();
            return new(SyncResponseHandlingResult.OK);
        }

        public override bool IsMultiFeed => false;

        public override AllocationContexts Contexts => AllocationContexts.Blocks;
    }
}
