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
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.ParallelSync
{
    public abstract class SyncFeed<T> : ISyncFeed<T>
    {
        private readonly TaskCompletionSource _taskCompletionSource = new();
        public abstract Task<T> PrepareRequest();
        public abstract SyncResponseHandlingResult HandleResponse(T response, PeerInfo peer = null);
        public abstract bool IsMultiFeed { get; }
        public abstract AllocationContexts Contexts { get; }
        public int FeedId { get; } = FeedIdProvider.AssignId();
        public SyncFeedState CurrentState { get; private set; }
        public event EventHandler<SyncFeedStateEventArgs>? StateChanged;
        
        private void ChangeState(SyncFeedState newState)
        {
            if (CurrentState == SyncFeedState.Finished)
            {
                throw new InvalidOperationException($"{GetType().Name} has already finished and cannot be {newState} again.");
            }

            CurrentState = newState;
            StateChanged?.Invoke(this, new SyncFeedStateEventArgs(newState));

            if (CurrentState == SyncFeedState.Finished)
            {
                _taskCompletionSource.SetResult();
            }
        }

        public void Activate() => ChangeState(SyncFeedState.Active);

        public void Finish() => ChangeState(SyncFeedState.Finished);
        public Task FeedTask => _taskCompletionSource.Task;

        public void FallAsleep() => ChangeState(SyncFeedState.Dormant);
    }
}
