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
using Nethermind.Blockchain.Synchronization;
using Nethermind.Logging;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.Blocks
{
    public class FastSyncFeed : SyncFeed<BlocksRequest>, IDisposable
    {
        private readonly ISyncModeSelector _syncModeSelector;
        private readonly ISyncConfig _syncConfig;

        public FastSyncFeed(ISyncModeSelector syncModeSelector, ISyncConfig syncConfig, ILogManager logManager)
            : base(logManager)
        {
            _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));
            _syncModeSelector = syncModeSelector ?? throw new ArgumentNullException(nameof(syncModeSelector));

            DownloaderOptions options = BuildOptions();
            _blocksRequest = new BlocksRequest(options, MultiSyncModeSelector.FastSyncLag);
            
            _syncModeSelector.Changed += SyncModeSelectorOnChanged;
        }

        private void SyncModeSelectorOnChanged(object sender, SyncModeChangedEventArgs e)
        {
            if ((e.Current & SyncMode.FastSync) == SyncMode.FastSync)
            {
                Activate();
            }
        }

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

        private BlocksRequest _blocksRequest;

        public override Task<BlocksRequest> PrepareRequest()
        {
            if ((_syncModeSelector.Current & SyncMode.FastSync) == SyncMode.FastSync)
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