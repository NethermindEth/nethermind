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
        throw new InvalidOperationException("null sync feed should not be used");
    }

    public SyncResponseHandlingResult HandleResponse(T response, PeerInfo? peer = null)
    {
        throw new InvalidOperationException("null sync feed should not be used");
    }

    public bool IsMultiFeed { get; }
    public AllocationContexts Contexts { get; }
    public void Activate()
    {
        throw new InvalidOperationException("null sync feed should not be used");
    }

    public void Finish()
    {
        throw new InvalidOperationException("null sync feed should not be used");
    }

    public Task FeedTask => Task.CompletedTask;
    public void SyncModeSelectorOnChanged(SyncMode current)
    {
        throw new InvalidOperationException("null sync feed should not be used");
    }

    public bool IsFinished { get; } = true;
}
