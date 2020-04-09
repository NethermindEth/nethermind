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

namespace Nethermind.Synchronization.TotalSync
{
    public abstract class SyncFeed<T> : ISyncFeed<T>
    {
        public abstract Task<T> PrepareRequest();
        public abstract SyncBatchResponseHandlingResult HandleResponse(T response);

        public abstract bool IsMultiFeed { get; }
        public SyncFeedState CurrentState { get; private set; }
        public event EventHandler<SyncFeedStateEventArgs> StateChanged;
        private void ChangeState(SyncFeedState newState)
        {
            CurrentState = newState;
            StateChanged?.Invoke(this, new SyncFeedStateEventArgs(newState));
        }

        public void Activate()
        {
            ChangeState(SyncFeedState.Active);
        }
        
        protected void Finish()
        {
            ChangeState(SyncFeedState.Finished);
        }
        
        protected void FallAsleep()
        {
            ChangeState(SyncFeedState.Dormant);
        }
    }
}