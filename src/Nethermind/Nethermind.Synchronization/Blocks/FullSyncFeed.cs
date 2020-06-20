//  Copyright (c) 2018 Demerzel Solutions Limited
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
using Nethermind.Logging;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.Blocks
{
    public class FullSyncFeed : SyncFeed<BlocksRequest>, IDisposable
    {
        private readonly ISyncModeSelector _syncModeSelector;

        private BlocksRequest _blocksRequest;

        public FullSyncFeed(ISyncModeSelector syncModeSelector, ILogManager logManager)
            : base(logManager)
        {
            _syncModeSelector = syncModeSelector ?? throw new ArgumentNullException(nameof(syncModeSelector));

            DownloaderOptions options = BuildOptions();
            _blocksRequest = new BlocksRequest(options);

            _syncModeSelector.Changed += SyncModeSelectorOnChanged;
        }

        private static bool ShouldBeActive(SyncMode syncMode)
        {
            return (syncMode & (SyncMode.Full | SyncMode.Beam)) != SyncMode.None;
        }

        private void SyncModeSelectorOnChanged(object sender, SyncModeChangedEventArgs e)
        {
            // we will download blocks for processing both in beam sync and full sync mode
            if (ShouldBeActive(e.Current))
            {
                Activate();
            }
        }

        private static DownloaderOptions BuildOptions()
        {
            return DownloaderOptions.WithBodies | DownloaderOptions.Process;
        }

        public override Task<BlocksRequest> PrepareRequest()
        {
            if (ShouldBeActive(_syncModeSelector.Current))
            {
                return Task.FromResult(_blocksRequest);
            }

            return Task.FromResult((BlocksRequest) null);
        }

        public override SyncResponseHandlingResult HandleResponse(BlocksRequest response)
        {
            FallAsleep();
            return SyncResponseHandlingResult.OK;
        }

        public override bool IsMultiFeed => false;
        
        public override AllocationContexts Contexts => AllocationContexts.Blocks;

        public void Dispose()
        {
            _syncModeSelector.Changed -= SyncModeSelectorOnChanged;
        }
    }
}