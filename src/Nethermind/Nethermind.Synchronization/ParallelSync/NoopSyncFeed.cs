// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.ParallelSync;

public class NoopSyncFeed<T> : ISyncFeed<T>
{
    public SyncFeedState CurrentState { get; }

#pragma warning disable CS0067
    public event EventHandler<SyncFeedStateEventArgs>? StateChanged;
#pragma warning disable

    public Task<T> PrepareRequest(CancellationToken token = default)
    {
        return Task.FromResult<T>(default);
    }

    public SyncResponseHandlingResult HandleResponse(T response, PeerInfo? peer = null)
    {
        return SyncResponseHandlingResult.NotAssigned;
    }

    public bool IsMultiFeed { get; }
    public AllocationContexts Contexts { get; }
    public void Activate()
    {
    }

    public void Finish()
    {
    }

    public Task FeedTask => Task.CompletedTask;
    public void SyncModeSelectorOnChanged(SyncMode current)
    {
    }

    public bool IsFinished { get; } = true;
    public string FeedName => nameof(NoopSyncFeed<T>);
}
