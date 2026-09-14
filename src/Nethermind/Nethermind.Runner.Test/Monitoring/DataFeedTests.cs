// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Runner.Monitoring;
using Nethermind.Synchronization.Peers;
using Nethermind.TxPool;
using NSubstitute;
using NSubstitute.Core;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Monitoring;

[NonParallelizable]
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

    [TestCase(null, "processed,log,forkChoice,txLinks,system,peers")]
    [TestCase("", "processed,log,forkChoice,txLinks,system,peers")]
    [TestCase("processed", "processed")]
    [TestCase(" Processed , forkchoice,processed", "processed,forkChoice")]
    [TestCase("nodeData,bogus", "processed,log,forkChoice,txLinks,system,peers")]
    public void Requested_events_resolve_to_streamed_entry_types(string? events, string expected) =>
        Assert.That(string.Join(',', DataFeed.ParseRequestedEvents(events)), Is.EqualTo(expected));

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

    [Test]
    public async Task Event_subscriptions_only_prepare_requested_data()
    {
        await AssertFeedSubscriptionAsync("?events=processed", expectForkChoice: false);
        await AssertFeedSubscriptionAsync(null, expectForkChoice: true);
    }

    private static async Task AssertFeedSubscriptionAsync(string? query, bool expectForkChoice)
    {
        (FieldInfo Field, object? Original, LineInterceptingTextWriter Installed)? ownedConsoleWriter = EnsureConsoleHelpersInitialized();

        using CancellationTokenSource lifetime = new();
        lifetime.Cancel();
        using CancellationTokenSource feedCancellation = new();

        IBlockchainProcessor blockchainProcessor = Substitute.For<IBlockchainProcessor>();
        IMainProcessingContext mainProcessingContext = Substitute.For<IMainProcessingContext>();
        mainProcessingContext.BlockchainProcessor.Returns(blockchainProcessor);
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        IReceiptFinder receiptFinder = Substitute.For<IReceiptFinder>();
        Block head = Build.A.Block.TestObject;
        TaskCompletionSource receiptPrepared = new(TaskCreationOptions.RunContinuationsAsynchronously);
        receiptFinder.Get(Arg.Any<Block>(), Arg.Any<bool>(), Arg.Any<bool>()).Returns([]);
        receiptFinder.WhenForAnyArgs(finder => finder.Get(default!, default, default))
            .Do(_ => receiptPrepared.TrySetResult());

        DataFeed dataFeed = new(
            Substitute.For<ITxPool>(),
            Substitute.For<ISpecProvider>(),
            receiptFinder,
            blockTree,
            Substitute.For<ISyncPeerPool>(),
            mainProcessingContext,
            LimboLogs.Instance,
            lifetime.Token);

        using RecordingResponseBody responseBody = new();
        DefaultHttpContext httpContext = new();
        httpContext.Request.QueryString = query is null ? QueryString.Empty : new QueryString(query);
        httpContext.Response.Body = responseBody;

        Task? feed = null;
        try
        {
            feed = dataFeed.ProcessingFeedAsync(httpContext, feedCancellation.Token);
            for (int attempt = 0; attempt < 200 && !responseBody.ProcessedWritten.Task.IsCompleted; attempt++)
            {
                blockchainProcessor.NewProcessingStatistics += Raise.Event<EventHandler<BlockStatistics>>(null, new BlockStatistics());
                await Task.WhenAny(responseBody.ProcessedWritten.Task, Task.Delay(TimeSpan.FromMilliseconds(25)));
            }
            await responseBody.ProcessedWritten.Task.WaitAsync(TimeSpan.FromSeconds(1));

            blockTree.OnForkChoiceUpdated += Raise.Event<EventHandler<IBlockTree.ForkChoiceUpdateEventArgs>>(null, new IBlockTree.ForkChoiceUpdateEventArgs(head, 0, 0));
            if (expectForkChoice)
            {
                await responseBody.ForkChoiceWritten.Task.WaitAsync(TimeSpan.FromSeconds(1));
                await receiptPrepared.Task.WaitAsync(TimeSpan.FromSeconds(1));
            }
            else
            {
                Assert.That(async () => await receiptPrepared.Task.WaitAsync(TimeSpan.FromMilliseconds(200)), Throws.TypeOf<TimeoutException>());
                Assert.That(responseBody.EventCount("event: forkChoice"), Is.Zero);
            }

            feedCancellation.Cancel();
            await feed.WaitAsync(TimeSpan.FromSeconds(1));
            AssertNoSubscribers(dataFeed);

            int processedEvents = responseBody.EventCount("event: processed");
            blockchainProcessor.NewProcessingStatistics += Raise.Event<EventHandler<BlockStatistics>>(null, new BlockStatistics());
            Assert.That(responseBody.EventCount("event: processed"), Is.EqualTo(processedEvents));

            if (expectForkChoice)
            {
                receiptFinder.Received(1).Get(Arg.Any<Block>(), Arg.Any<bool>(), Arg.Any<bool>());
            }
        }
        finally
        {
            feedCancellation.Cancel();
            try
            {
                if (feed is not null) await feed.WaitAsync(TimeSpan.FromSeconds(1));
            }
            finally
            {
                RemoveConsoleSubscription(dataFeed);
                ownedConsoleWriter?.Field.SetValue(null, ownedConsoleWriter.Value.Original);
                ownedConsoleWriter?.Installed?.Dispose();
            }
        }
    }

    private static (FieldInfo Field, object? Original, LineInterceptingTextWriter Installed)? EnsureConsoleHelpersInitialized()
    {
        FieldInfo field = typeof(ConsoleHelpers).GetField("_interceptingWriter", BindingFlags.Static | BindingFlags.NonPublic)!;
        object? original = field.GetValue(null);
        if (original is not null) return null;

        LineInterceptingTextWriter installed = new(TextWriter.Null);
        field.SetValue(null, installed);
        return (field, original, installed);
    }

    private static void AssertNoSubscribers(DataFeed dataFeed)
    {
        FieldInfo field = typeof(DataFeed).GetField("_subscribersByType", BindingFlags.Instance | BindingFlags.NonPublic)!;
        long[] subscribers = (long[])field.GetValue(dataFeed)!;
        Assert.That(subscribers, Is.All.EqualTo(0));
    }

    private static void RemoveConsoleSubscription(DataFeed dataFeed)
    {
        MethodInfo method = typeof(DataFeed).GetMethod("OnConsoleLineWritten", BindingFlags.Instance | BindingFlags.NonPublic)!;
        EventHandler<string> handler = (EventHandler<string>)method.CreateDelegate(typeof(EventHandler<string>), dataFeed);
        ConsoleHelpers.LineWritten -= handler;
    }

    private sealed class RecordingResponseBody : Stream
    {
        private readonly object _lock = new();
        private readonly StringBuilder _content = new();

        public TaskCompletionSource ProcessedWritten { get; } = NewSignal();
        public TaskCompletionSource ForkChoiceWritten { get; } = NewSignal();

        public int EventCount(string eventName)
        {
            lock (_lock)
            {
                int count = 0;
                int start = 0;
                while ((start = _content.ToString().IndexOf(eventName, start, StringComparison.Ordinal)) >= 0)
                {
                    count++;
                    start += eventName.Length;
                }

                return count;
            }
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => Record(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer) => Record(buffer);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Record(buffer.AsSpan(offset, count));
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Record(buffer.Span);
            return ValueTask.CompletedTask;
        }

        private void Record(ReadOnlySpan<byte> buffer)
        {
            lock (_lock)
            {
                _content.Append(Encoding.UTF8.GetString(buffer));
                string content = _content.ToString();
                if (content.Contains("event: processed\ndata: {", StringComparison.Ordinal)) ProcessedWritten.TrySetResult();
                if (content.Contains("event: forkChoice\ndata: {", StringComparison.Ordinal)) ForkChoiceWritten.TrySetResult();
            }
        }

        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
