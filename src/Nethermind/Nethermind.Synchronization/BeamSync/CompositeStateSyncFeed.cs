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
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Logging;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.BeamSync
{
    public class CompositeStateSyncFeed : SyncFeed<MultiStateSyncBatch>
    {
        private readonly ISyncFeed<StateSyncBatch>[] _subFeeds;
        private ILogger _logger;

        public CompositeStateSyncFeed(ILogManager logManager, params ISyncFeed<StateSyncBatch>[] subFeeds)
            : base(logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _subFeeds = subFeeds;
            foreach (ISyncFeed<StateSyncBatch> syncFeed in _subFeeds)
            {
                syncFeed.StateChanged += OnSubFeedStateChanged;
            }
        }

        private void OnSubFeedStateChanged(object sender, EventArgs e)
        {
            ISyncFeed<StateSyncBatch> child = (ISyncFeed<StateSyncBatch>) sender;
            if (child.CurrentState == SyncFeedState.Active)
            {
                Activate();
                return;
            }

            bool areAllFinished = true;
            foreach (ISyncFeed<StateSyncBatch> subFeed in _subFeeds)
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

        public override async ValueTask<MultiStateSyncBatch> PrepareRequest()
        {
            bool someWereNotEmpty;
            MultiStateSyncBatch result = null;
            List<StateSyncBatch> batches = null;

            // we will try to fill the multi batch as long as the sub feed threads keep adding items
            do
            {
                someWereNotEmpty = false;
                for (int subFeedIndex = 0; subFeedIndex < _subFeeds.Length; subFeedIndex++)
                {
                    ISyncFeed<StateSyncBatch> subFeed = _subFeeds[subFeedIndex];

                    // we ignore 
                    if (subFeed.CurrentState == SyncFeedState.Active)
                    {
                        StateSyncBatch batch = await subFeed.PrepareRequest();
                        if (batch != null)
                        {
                            batches ??= new List<StateSyncBatch>();
                            batches.Add(batch);
                            someWereNotEmpty = true;
                        }
                    }
                }
            } while (someWereNotEmpty);

            if (batches != null)
            {
                result = new MultiStateSyncBatch(batches);
                // if(_logger.IsWarn) _logger.Warn($"Combining {batches.Count} with {result.AllRequestedNodes.Count()} requests into one multibatch");
            }
            
            return result;
        }

        public override SyncResponseHandlingResult HandleResponse(MultiStateSyncBatch response)
        {
            SyncResponseHandlingResult result = SyncResponseHandlingResult.OK; 
            foreach (ISyncFeed<StateSyncBatch> subFeed in _subFeeds)
            {
                foreach (StateSyncBatch stateSyncBatch in response.Batches)
                {
                    SyncResponseHandlingResult subResult = SyncResponseHandlingResult.NoProgress;
                    if (subFeed.FeedId == stateSyncBatch.FeedId)
                    {
                        subResult = subFeed.HandleResponse(stateSyncBatch);
                    }
                    
                    if (subResult > result)
                    {
                        result = subResult;
                    }
                }
            }

            return result;
        }

        // false for now but probably true
        public override bool IsMultiFeed => false;

        public override AllocationContexts Contexts => AllocationContexts.State;
    }
}