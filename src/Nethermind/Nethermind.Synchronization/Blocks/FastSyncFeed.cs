//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
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

        public override Task<BlocksRequest> PrepareRequest() => Task.FromResult(ShouldBeActive() ? _blocksRequest : null!);

        public override SyncResponseHandlingResult HandleResponse(BlocksRequest response)
        {
            FallAsleep();
            return SyncResponseHandlingResult.OK;
        }

        public override bool IsMultiFeed => false;
        
        public override AllocationContexts Contexts => AllocationContexts.Blocks;
    }
}
