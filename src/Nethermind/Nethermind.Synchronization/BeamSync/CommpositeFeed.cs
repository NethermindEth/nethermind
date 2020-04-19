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
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.Synchronization.BeamSync
{
    public class CompositeFeed<T> : SyncFeed<T>
    {
        private readonly ISyncFeed<T>[] _subFeeds;
        private ILogger _logger;

        public CompositeFeed(ILogManager logManager, params ISyncFeed<T>[] subFeeds)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _subFeeds = subFeeds;
            foreach (ISyncFeed<T> dataConsumer in _subFeeds)
            {
                dataConsumer.StateChanged += OnSubFeedStateChanged;
            }
        }

        private void OnSubFeedStateChanged(object sender, EventArgs e)
        {
            ISyncFeed<StateSyncBatch> child = (ISyncFeed<StateSyncBatch>) sender;
            if (child.CurrentState == SyncFeedState.Active)
            {
                Activate();
            }
        }

        public override Task<T> PrepareRequest()
        {
            foreach (ISyncFeed<T> syncFeed in _subFeeds)
            {
                if (syncFeed.CurrentState == SyncFeedState.Active)
                {
                    T batch = syncFeed.PrepareRequest().Result;
                    if (batch != null)
                    {
                        return Task.FromResult(batch);
                    }
                }
            }

            return null;
        }

        public override SyncResponseHandlingResult HandleResponse(T batch)
        {
            foreach (ISyncFeed<T> subFeed in _subFeeds)
            {
                
            }
            
            return SyncResponseHandlingResult.OK;
        }

        // false for now but probably true
        public override bool IsMultiFeed => false;
    }
}