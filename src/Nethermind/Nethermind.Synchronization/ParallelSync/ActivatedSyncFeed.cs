// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;

namespace Nethermind.Synchronization.ParallelSync
{
    public abstract class ActivatedSyncFeed<T> : SyncFeed<T>, IDisposable
    {
        private readonly bool _disposed = false;

        protected ActivatedSyncFeed()
        {
            StateChanged += OnStateChanged;
        }

        private void OnStateChanged(object? sender, SyncFeedStateEventArgs e)
        {
            if (e.NewState == SyncFeedState.Finished)
            {
                Dispose();
            }
        }

        public override void SyncModeSelectorOnChanged(SyncMode current)
        {
            if (_disposed) return;
            if (ShouldBeActive(current))
            {
                Task.Run(() =>
                {
                    InitializeFeed();
                    Activate();
                });
            }

            if (ShouldBeDormant(current))
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
            StateChanged -= OnStateChanged;
        }

        public virtual void InitializeFeed() { }
    }
}
