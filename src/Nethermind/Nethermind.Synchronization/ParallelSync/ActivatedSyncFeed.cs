// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
                InitializeFeed();
                Activate();
            }

            if (ShouldBeDormant(e.Current))
            {
                FallAsleep();
            }
        }

        private bool ShouldBeActive(SyncMode current)
            => CurrentState == SyncFeedState.Dormant && (current & ActivationSyncModes) != SyncMode.None;

        private bool ShouldBeDormant(SyncMode current)
            => CurrentState == SyncFeedState.Active && (current & ActivationSyncModes) == SyncMode.None;

        protected abstract SyncMode ActivationSyncModes { get; }

        public virtual void Dispose()
        {
            _syncModeSelector.Changed -= SyncModeSelectorOnChanged;
            StateChanged -= OnStateChanged;
        }

        public virtual void InitializeFeed() { }
    }
}
