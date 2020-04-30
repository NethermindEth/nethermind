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
// 

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Logging;
using Nethermind.Synchronization.BeamSync;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.ParallelSync
{
    [TestFixture]
    public class CompositeStateSyncFeedTests
    {
        [Test]
        public async Task Returns_null_when_none_active()
        {
            var subFeed0 = CreateFeed(1, 1);
            var subFeed1 = CreateFeed(1, 1);

            CompositeStateSyncFeed feed = new CompositeStateSyncFeed(LimboLogs.Instance, subFeed0, subFeed1);
            IStateSyncBatch request = await feed.PrepareRequest();

            request.Should().Be(null);
        }

        [Test]
        public async Task Can_combine_two_sources()
        {
            var subFeed0 = CreateFeed(1, 1, SyncFeedState.Active);
            var subFeed1 = CreateFeed(1, 2, SyncFeedState.Active);

            CompositeStateSyncFeed feed = new CompositeStateSyncFeed(LimboLogs.Instance, subFeed0, subFeed1);
            IStateSyncBatch request0 = await feed.PrepareRequest();

            request0.AllRequestedNodes.Should().HaveCount(3);
        }

        [Test]
        public async Task Can_combine_sources_and_batches()
        {
            var subFeed0 = CreateFeed(2, 3, SyncFeedState.Active);
            var subFeed1 = CreateFeed(5, 7, SyncFeedState.Active);

            CompositeStateSyncFeed feed = new CompositeStateSyncFeed(LimboLogs.Instance, subFeed0, subFeed1);
            IStateSyncBatch request0 = await feed.PrepareRequest();

            request0.AllRequestedNodes.Should().HaveCount(2 * 3 + 5 * 7);
        }
        
        [Test]
        public async Task Can_handle_multi_response()
        {
            var subFeed0 = CreateFeed(2, 3, SyncFeedState.Active);
            var subFeed1 = CreateFeed(5, 7, SyncFeedState.Active);

            CompositeStateSyncFeed feed = new CompositeStateSyncFeed(LimboLogs.Instance, subFeed0, subFeed1);
            MultiStateSyncBatch request = await feed.PrepareRequest();
            
            request.Responses = new byte[2 * 3 + 5 * 7][];
            feed.HandleResponse(request).Should().Be(SyncResponseHandlingResult.OK);
        }

        private static ISyncFeed<StateSyncBatch> CreateFeed(int times, int length, SyncFeedState state = SyncFeedState.Dormant)
        {
            Queue<StateSyncBatch> batches = new Queue<StateSyncBatch>();
            for (int i = 0; i < times; i++)
            {
                batches.Enqueue(new StateSyncBatch {FeedId = 0, RequestedNodes = new StateSyncItem[length]});
            }

            ISyncFeed<StateSyncBatch> feed = Substitute.For<ISyncFeed<StateSyncBatch>>();
            feed.PrepareRequest().Returns(ci => batches.Any() ? batches.Dequeue() : null);
            feed.CurrentState.Returns(state);
            return feed;
        }
    }
}