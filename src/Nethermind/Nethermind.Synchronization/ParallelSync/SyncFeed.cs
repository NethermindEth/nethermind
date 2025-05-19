// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.ParallelSync
{
    public abstract class SyncFeed<T> : ISyncFeed<T>
    {
        private TaskCompletionSource? _taskCompletionSource = null;
        public abstract Task<T> PrepareRequest(CancellationToken token = default);
        public abstract SyncResponseHandlingResult HandleResponse(T response, PeerInfo peer = null);
        public abstract bool IsMultiFeed { get; }
        public abstract AllocationContexts Contexts { get; }
        public SyncFeedState CurrentState { get; private set; }
        public event EventHandler<SyncFeedStateEventArgs>? StateChanged;

        private void ChangeState(SyncFeedState newState)
        {
            if (newState == SyncFeedState.Active)
            {
                _taskCompletionSource ??= new TaskCompletionSource();
            }

            if (CurrentState == SyncFeedState.Finished && newState == SyncFeedState.Finished)
            {
                return;
            }

            if (CurrentState == SyncFeedState.Finished)
            {
                return;
            }

            CurrentState = newState;
            StateChanged?.Invoke(this, new SyncFeedStateEventArgs(newState));

            if (newState == SyncFeedState.Finished)
            {
                _taskCompletionSource?.SetResult();
            }
        }

        public void Activate() => ChangeState(SyncFeedState.Active);

        public virtual void Finish()
        {
            ChangeState(SyncFeedState.Finished);
        }
        public Task FeedTask => _taskCompletionSource?.Task ?? Task.CompletedTask;
        public abstract void SyncModeSelectorOnChanged(SyncMode current);
        public abstract bool IsFinished { get; }

        public virtual void FallAsleep() => ChangeState(SyncFeedState.Dormant);
    }
}
