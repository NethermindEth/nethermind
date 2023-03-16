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
        int FeedId { get; }
        SyncFeedState CurrentState { get; }
        event EventHandler<SyncFeedStateEventArgs> StateChanged;
        ValueTask<T?> PrepareRequest(CancellationToken token = default);
        ValueTask<SyncResponseHandlingResult> HandleResponse(T response, PeerInfo? peer = null);

        /// <summary>
        /// Multifeed can prepare and handle multiple requests concurrently.
        /// </summary>
        bool IsMultiFeed { get; }

        AllocationContexts Contexts { get; }
        void Activate();
        void Finish();
        Task FeedTask { get; }
    }
}
