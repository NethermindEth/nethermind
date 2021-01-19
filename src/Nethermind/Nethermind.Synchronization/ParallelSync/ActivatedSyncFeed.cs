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
// 

using System;

namespace Nethermind.Synchronization.ParallelSync
{
    public abstract class ActivatedSyncFeed<T> : SyncFeed<T>, IDisposable
    {
        private readonly ISyncModeSelector _syncModeSelector;
        
        protected ActivatedSyncFeed(ISyncModeSelector syncModeSelector)
        {
            _syncModeSelector = syncModeSelector ?? throw new ArgumentNullException(nameof(syncModeSelector));
            _syncModeSelector.Changed += SyncModeSelectorOnChanged;
            StateChanged += OnStateChanged;
        }

        private void OnStateChanged(object? sender, SyncFeedStateEventArgs e)
        {
            if (e.NewState == SyncFeedState.Finished)
            {
                Dispose();
            }
        }

        private void SyncModeSelectorOnChanged(object? sender, SyncModeChangedEventArgs e)
        {
            if (ShouldBeActive(e.Current))
            {
                Activate();
            }
        }

        protected bool ShouldBeActive() => ShouldBeActive(_syncModeSelector.Current);

        private bool ShouldBeActive(SyncMode current)
            => CurrentState != SyncFeedState.Finished && (current & ActivationSyncModes) != SyncMode.None;

        protected abstract SyncMode ActivationSyncModes { get; }

        public virtual void Dispose()
        {
            _syncModeSelector.Changed -= SyncModeSelectorOnChanged;
            StateChanged -= OnStateChanged;
        }
    }
}
