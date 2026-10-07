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
using Autofac;
using Microsoft.AspNetCore.Http;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Modules;
using Nethermind.Logging;
using Nethermind.Runner.Monitoring;
using Nethermind.Specs.Forks;
using Nethermind.Synchronization.Peers;
using Nethermind.TxPool;
using NSubstitute;
using NSubstitute.Core;
using NSubstitute.ExceptionExtensions;
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

    [TestCase("?events=processed", false)]
    [TestCase(null, true)]
    [CancelAfter(30_000)]
    public async Task Event_subscriptions_only_prepare_requested_data(string? query, bool expectForkChoice, CancellationToken cancellationToken)
    {
        await using FeedHarness harness = await FeedHarness.OpenAsync(query, cancellationToken);

        Block head = harness.BlockTree.Head!;
        object forkChoiceBeforeRaise = harness.ForkChoiceCompletion;
        harness.BlockTree.ForkChoiceUpdated(head.Hash, head.Hash);

        if (expectForkChoice)
        {
            Assert.That(harness.ForkChoiceCompletion, Is.Not.SameAs(forkChoiceBeforeRaise));
            await harness.ResponseBody.ForkChoiceWritten.Task.WaitAsync(cancellationToken);
            harness.ReceiptFinder.Received(1).Get(head, Arg.Any<bool>(), Arg.Any<bool>());
        }
        else
        {
            // The handler swaps the completion source before it queues any work, so an unchanged source proves the
            // raise returned at the subscriber gate with nothing left in flight.
            Assert.That(harness.ForkChoiceCompletion, Is.SameAs(forkChoiceBeforeRaise));
            harness.ReceiptFinder.DidNotReceive().Get(head, Arg.Any<bool>(), Arg.Any<bool>());
        }

        await harness.CloseFeedAsync(cancellationToken);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.SubscriberCounts, Is.All.EqualTo(0));
            Assert.That(harness.ResponseBody.EventCount("event: forkChoice"), expectForkChoice ? Is.GreaterThan(0) : Is.Zero);
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Failed_fork_choice_preparation_is_skipped_by_subscribers(CancellationToken cancellationToken)
    {
        await using FeedHarness harness = await FeedHarness.OpenAsync(null, cancellationToken);

        Block failing = harness.BlockTree.Head!;
        harness.ReceiptFinder.Get(failing, Arg.Any<bool>(), Arg.Any<bool>()).Throws<InvalidOperationException>();
        harness.BlockTree.ForkChoiceUpdated(failing.Hash, failing.Hash);

        Block next = await harness.BlockchainUtil.AddBlockAndWaitForHead(false, cancellationToken);
        harness.BlockTree.ForkChoiceUpdated(next.Hash, next.Hash);

        await harness.ResponseBody.ForkChoiceWritten.Task.WaitAsync(cancellationToken);
        await harness.CloseFeedAsync(cancellationToken);

        harness.ReceiptFinder.Received(1).Get(failing, Arg.Any<bool>(), Arg.Any<bool>());
        harness.ReceiptFinder.Received(1).Get(next, Arg.Any<bool>(), Arg.Any<bool>());
        Assert.That(harness.ResponseBody.EventCount("event: forkChoice"), Is.EqualTo(1));
    }

    private static readonly FieldInfo ConsoleWriterField =
        typeof(ConsoleHelpers).GetField("_interceptingWriter", BindingFlags.Static | BindingFlags.NonPublic)!;

    // The feed replays recent console lines on connect, which needs the interceptor the runner installs at startup.
    private static LineInterceptingTextWriter? InstallConsoleWriterIfMissing()
    {
        if (ConsoleWriterField.GetValue(null) is not null) return null;

        LineInterceptingTextWriter installed = new(TextWriter.Null);
        ConsoleWriterField.SetValue(null, installed);
        return installed;
    }

    private static void RemoveConsoleSubscription(DataFeed dataFeed)
    {
        MethodInfo method = typeof(DataFeed).GetMethod("OnConsoleLineWritten", BindingFlags.Instance | BindingFlags.NonPublic)!;
        EventHandler<string> handler = (EventHandler<string>)method.CreateDelegate(typeof(EventHandler<string>), dataFeed);
        ConsoleHelpers.LineWritten -= handler;
    }

    /// <summary>A data feed over the production modules, with the receipt finder as the only substitute, opened as an SSE subscription.</summary>
    private sealed class FeedHarness : IAsyncDisposable
    {
        private const int MaxReadinessBlocks = 30;

        private readonly LineInterceptingTextWriter? _installedConsoleWriter = InstallConsoleWriterIfMissing();
        private readonly IContainer _container;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly CancellationTokenSource _feedCancellation = new();
        private readonly DefaultHttpContext _httpContext = new();
        private Task? _feed;

        private FeedHarness(string? query)
        {
            ReceiptFinder = Substitute.For<IReceiptFinder>();
            ReceiptFinder.Get(Arg.Any<Block>(), Arg.Any<bool>(), Arg.Any<bool>()).Returns([]);
            _container = new ContainerBuilder()
                .AddModule(new TestNethermindModule(Cancun.Instance))
                .AddSingleton<IReceiptFinder>(ReceiptFinder)
                .Build();
            BlockTree = _container.Resolve<IBlockTree>();
            BlockchainUtil = _container.Resolve<TestBlockchainUtil>();

            // A cancelled lifetime keeps the feed's periodic refresh loops out of the test.
            _lifetime.Cancel();
            DataFeed = new DataFeed(
                _container.Resolve<ITxPool>(),
                _container.Resolve<ISpecProvider>(),
                ReceiptFinder,
                BlockTree,
                _container.Resolve<ISyncPeerPool>(),
                _container.Resolve<IMainProcessingContext>(),
                LimboLogs.Instance,
                _lifetime.Token);

            _httpContext.Request.QueryString = query is null ? QueryString.Empty : new QueryString(query);
            _httpContext.Response.Body = ResponseBody;
        }

        public IBlockTree BlockTree { get; }
        public TestBlockchainUtil BlockchainUtil { get; }
        public IReceiptFinder ReceiptFinder { get; }
        public DataFeed DataFeed { get; }
        public RecordingResponseBody ResponseBody { get; } = new();

        public object ForkChoiceCompletion =>
            typeof(DataFeed).GetField("_forkChoice", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(DataFeed)!;

        public long[] SubscriberCounts =>
            (long[])typeof(DataFeed).GetField("_subscribersByType", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(DataFeed)!;

        /// <summary>Opens the feed for <paramref name="query"/> and returns once a processing report has reached it.</summary>
        public static async Task<FeedHarness> OpenAsync(string? query, CancellationToken cancellationToken)
        {
            FeedHarness harness = new(query);
            try
            {
                await harness.StartAsync(cancellationToken);
                return harness;
            }
            catch
            {
                await harness.DisposeAsync();
                throw;
            }
        }

        public async Task CloseFeedAsync(CancellationToken cancellationToken)
        {
            _feedCancellation.Cancel();
            await _feed!.WaitAsync(cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            _feedCancellation.Cancel();
            try
            {
                if (_feed is not null) await _feed.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                RemoveConsoleSubscription(DataFeed);
                if (_installedConsoleWriter is not null)
                {
                    ConsoleWriterField.SetValue(null, null);
                    _installedConsoleWriter.Dispose();
                }

                await _container.DisposeAsync();
                ResponseBody.Dispose();
                _feedCancellation.Dispose();
                _lifetime.Dispose();
            }
        }

        private async Task StartAsync(CancellationToken cancellationToken)
        {
            await _container.Resolve<PseudoNethermindRunner>().StartBlockProcessing(cancellationToken);
            _feed = DataFeed.ProcessingFeedAsync(_httpContext, _feedCancellation.Token);

            // Processing statistics are reported at most once a second, so blocks are added until a report reaches the feed.
            for (int attempt = 0; !ResponseBody.ProcessedWritten.Task.IsCompleted; attempt++)
            {
                Assert.That(attempt, Is.LessThan(MaxReadinessBlocks), "no processed event reached the feed");
                Assert.That(_feed.IsCompleted, Is.False, "the feed ended before a processed event reached it");
                await BlockchainUtil.AddBlockAndWaitForHead(false, cancellationToken);
                await Task.WhenAny(ResponseBody.ProcessedWritten.Task, Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken));
            }
        }
    }

    private sealed class RecordingResponseBody : Stream
    {
        private readonly Lock _lock = new();
        private readonly StringBuilder _content = new();

        public TaskCompletionSource ProcessedWritten { get; } = NewSignal();
        public TaskCompletionSource ForkChoiceWritten { get; } = NewSignal();

        public int EventCount(string eventName)
        {
            string content;
            lock (_lock) content = _content.ToString();

            int count = 0;
            int start = 0;
            while ((start = content.IndexOf(eventName, start, StringComparison.Ordinal)) >= 0)
            {
                count++;
                start += eventName.Length;
            }

            return count;
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
                if (ProcessedWritten.Task.IsCompleted && ForkChoiceWritten.Task.IsCompleted) return;

                string content = _content.ToString();
                if (content.Contains("event: processed\ndata: {", StringComparison.Ordinal)) ProcessedWritten.TrySetResult();
                if (content.Contains("event: forkChoice\ndata: {", StringComparison.Ordinal)) ForkChoiceWritten.TrySetResult();
            }
        }

        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
