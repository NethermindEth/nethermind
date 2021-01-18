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
using Nethermind.Logging;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.BeamSync
{
    public class CompositeStateSyncFeed<T> : SyncFeed<T> where T : StateSyncBatch?
    {
        private readonly ISyncFeed<T>[] _subFeeds;
        private ILogger _logger;

        public CompositeStateSyncFeed(ILogManager logManager, params ISyncFeed<T>[] subFeeds)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _subFeeds = subFeeds;
            foreach (ISyncFeed<T> syncFeed in _subFeeds)
            {
                syncFeed.StateChanged += OnSubFeedStateChanged;
            }
        }

        private void OnSubFeedStateChanged(object? sender, EventArgs e)
        {
            ISyncFeed<StateSyncBatch>? child = (ISyncFeed<StateSyncBatch>?) sender;
            if (child == null)
            {
                if(_logger.IsDebug) _logger.Debug("Sub-feed state changed from a null feed");
                return;
            }
            
            if (child.CurrentState == SyncFeedState.Active)
            {
                Activate();
                return;
            }

            bool areAllFinished = true;
            foreach (ISyncFeed<T> subFeed in _subFeeds)
            {
                if (subFeed.CurrentState != SyncFeedState.Finished)
                {
                    areAllFinished = false;
                    break;
                }
            }

            if (areAllFinished)
            {
                Finish();
            }
        }

        public override async Task<T> PrepareRequest()
        {
            for (int subFeedIndex = 0; subFeedIndex < _subFeeds.Length; subFeedIndex++)
            {
                ISyncFeed<T> subFeed = _subFeeds[subFeedIndex];
                if (subFeed.CurrentState == SyncFeedState.Active)
                {
                    T batch = await subFeed.PrepareRequest();
                    if (batch != null)
                    {
                        return batch;
                    }
                }
            }

            return null!;
        }

        public override SyncResponseHandlingResult HandleResponse(T batch)
        {
            for (int subFeedIndex = 0; subFeedIndex < _subFeeds.Length; subFeedIndex++)
            {
                ISyncFeed<T> subFeed = _subFeeds[subFeedIndex];
                if (subFeed.FeedId == batch?.ConsumerId)
                {
                    subFeed.HandleResponse(batch);
                }
            }

            return SyncResponseHandlingResult.OK;
        }

        // false for now but probably true
        public override bool IsMultiFeed => false;
        
        public override AllocationContexts Contexts => AllocationContexts.State;
    }
}
