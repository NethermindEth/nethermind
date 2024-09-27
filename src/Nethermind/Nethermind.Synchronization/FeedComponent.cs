// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Synchronization.Blocks;
using Nethermind.Synchronization.ParallelSync;

public class FeedComponent<TBatch>(Lazy<ISyncFeed<TBatch>> feed, Lazy<SyncDispatcher<TBatch>> dispatcher, Lazy<ISyncDownloader<TBatch>> blockDownloader, ILifetimeScope lifetimeScope): IDisposable, IAsyncDisposable
{
    public ISyncFeed<TBatch> Feed => feed.Value;
    public SyncDispatcher<TBatch> Dispatcher => dispatcher.Value;
    public BlockDownloader BlockDownloader => (BlockDownloader)blockDownloader.Value;

    public void Dispose()
    {
        lifetimeScope.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await lifetimeScope.DisposeAsync();
    }
}
