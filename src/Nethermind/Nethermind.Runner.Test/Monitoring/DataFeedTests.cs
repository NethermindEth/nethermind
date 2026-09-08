// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Processing;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Runner.Monitoring;
using Nethermind.Synchronization.Peers;
using Nethermind.TxPool;
using NSubstitute;
using NSubstitute.Core;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Monitoring;

public class DataFeedTests
{
    [SetUp]
    public void Setup() => SubstitutionContext.Current?.ThreadContext?.DequeueAllArgumentSpecifications();

    [Test]
    public async Task Does_not_prepare_system_stats_without_subscribers()
    {
        using CancellationTokenSource lifetime = new();
        lifetime.Cancel();

        IMainProcessingContext mainProcessingContext = Substitute.For<IMainProcessingContext>();

        DataFeed dataFeed = new(
            Substitute.For<ITxPool>(),
            Substitute.For<ISpecProvider>(),
            Substitute.For<IReceiptFinder>(),
            Substitute.For<IBlockTree>(),
            Substitute.For<ISyncPeerPool>(),
            mainProcessingContext,
            LimboLogs.Instance,
            lifetime.Token);

        byte[]? data = await dataFeed.GetStatsTask(delayMs: 0);

        Assert.That(data, Is.Null);
    }

    [Test]
    public void Channel_subscription_stops_when_cancelled_before_data_arrives()
    {
        TaskCompletionSource<byte[]> source = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Channel<DataFeed.ChannelEntry> channel = Channel.CreateUnbounded<DataFeed.ChannelEntry>();
        using CancellationTokenSource cancellation = new();

        Task subscription = DataFeed.ChannelSubscribe(
            DataFeed.EntryType.system,
            () => source.Task,
            channel,
            cancellation.Token);

        cancellation.Cancel();

        Assert.That(async () => await subscription.WaitAsync(TimeSpan.FromSeconds(1)), Throws.Nothing);
    }
}
