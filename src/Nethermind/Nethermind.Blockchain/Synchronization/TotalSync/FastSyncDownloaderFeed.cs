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

using System.Threading.Tasks;

namespace Nethermind.Blockchain.Synchronization.TotalSync
{
    public class BlockDownloaderFeed : SyncFeed<BlockDownloadRequest>
    {
        private BlockDownloadRequest _request = new BlockDownloadRequest();
        
        public BlockDownloaderFeed(BlockDownloaderOptions options, bool downloadBodies, int numberOfLatestBlocksToIgnore)
        {
            _request.Options = options;
            _request.Style = downloadBodies ? BlockDownloadStyle.HeadersAndBodies : BlockDownloadStyle.HeadersOnly;
            _request.NumberOfLatestBlocksToBeIgnored = numberOfLatestBlocksToIgnore;
        }
        
        public override Task<BlockDownloadRequest> PrepareRequest()
        {
            return Task.FromResult(_request);
        }

        public override SyncBatchResponseHandlingResult HandleResponse(BlockDownloadRequest response)
        {
            ChangeState(SyncFeedState.Dormant);
            return SyncBatchResponseHandlingResult.OK;
        }

        public override void Activate()
        {
            ChangeState(SyncFeedState.Active);
        }
    }
}