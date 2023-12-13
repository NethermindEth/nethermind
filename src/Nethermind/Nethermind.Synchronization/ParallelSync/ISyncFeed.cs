// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.ParallelSync
{
    public interface ISyncFeed<T>
    {
        SyncFeedState CurrentState { get; }
        event EventHandler<SyncFeedStateEventArgs> StateChanged;
        Task<T> PrepareRequest(CancellationToken token = default);
        SyncResponseHandlingResult HandleResponse(T response, PeerInfo? peer = null);

        /// <summary>
        /// Multifeed can prepare and handle multiple requests concurrently.
        /// </summary>
        bool IsMultiFeed { get; }

        AllocationContexts Contexts { get; }
        void Activate();
        void Finish();
        Task FeedTask { get; }

        /// Called by MultiSyncModeSelector on sync mode change
        void SyncModeSelectorOnChanged(SyncMode current);

        /// Return true if not finished. May not run even if return true if MultiSyncModeSelector said no, probably
        /// because it's waiting for other sync or something.
        bool IsFinished { get; }
    }
}
