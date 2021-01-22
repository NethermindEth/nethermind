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
using Nethermind.Synchronization.Witness;

namespace Nethermind.Synchronization.ParallelSync
{
    public abstract class SyncFeed<T> : ISyncFeed<T>
    {
        public abstract Task<T> PrepareRequest();
        public abstract SyncResponseHandlingResult HandleResponse(T response);

        public abstract bool IsMultiFeed { get; }
        public abstract AllocationContexts Contexts { get; }
        public int FeedId { get; } = FeedIdProvider.AssignId();
        public SyncFeedState CurrentState { get; private set; }
        public event EventHandler<SyncFeedStateEventArgs>? StateChanged;
        
        private void ChangeState(SyncFeedState newState)
        {
            CurrentState = newState;
            StateChanged?.Invoke(this, new SyncFeedStateEventArgs(newState));
        }

        public void Activate()
        {
            if (CurrentState == SyncFeedState.Finished)
            {
                throw new InvalidOperationException($"{GetType().Name} has already finished and cannot be activated again.");
            }

            ChangeState(SyncFeedState.Active);
        }

        protected void Finish()
        {
            if (CurrentState == SyncFeedState.Finished)
            {
                throw new InvalidOperationException($"{GetType().Name} has already finished and cannot be finished again.");
            }
            
            ChangeState(SyncFeedState.Finished);
        }

        public void FallAsleep()
        {
            if (CurrentState == SyncFeedState.Finished)
            {
                throw new InvalidOperationException($"{GetType().Name} has already finished and cannot be put to sleep again.");
            }
            
            ChangeState(SyncFeedState.Dormant);
        }
    }
}
