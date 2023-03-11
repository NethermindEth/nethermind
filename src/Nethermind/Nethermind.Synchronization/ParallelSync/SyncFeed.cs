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
        private readonly TaskCompletionSource _taskCompletionSource = new();
        public abstract T PrepareRequest(CancellationToken token = default);
        public abstract ValueTask<SyncResponseHandlingResult> HandleResponse(T response, PeerInfo peer = null);
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

            if (newState == SyncFeedState.Finished)
            {
                _taskCompletionSource.SetResult();
            }
        }

        public void Activate() => ChangeState(SyncFeedState.Active);

        public void Finish()
        {
            ChangeState(SyncFeedState.Finished);
            GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        }
        public Task FeedTask => _taskCompletionSource.Task;

        public void FallAsleep() => ChangeState(SyncFeedState.Dormant);
    }
}
