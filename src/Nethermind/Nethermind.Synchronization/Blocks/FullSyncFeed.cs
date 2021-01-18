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

        protected override SyncMode ActivationSyncModes { get; } = SyncMode.Full | SyncMode.Beam;

        private static DownloaderOptions BuildOptions() => DownloaderOptions.WithBodies | DownloaderOptions.Process;

        // ReSharper disable once RedundantTypeArgumentsOfMethod
        public override Task<BlocksRequest?> PrepareRequest() => Task.FromResult<BlocksRequest?>(ShouldBeActive() ? _blocksRequest : null);

        public override SyncResponseHandlingResult HandleResponse(BlocksRequest? response)
        {
            FallAsleep();
            return SyncResponseHandlingResult.OK;
        }

        public override bool IsMultiFeed => false;
        
        public override AllocationContexts Contexts => AllocationContexts.Blocks;
    }
}
