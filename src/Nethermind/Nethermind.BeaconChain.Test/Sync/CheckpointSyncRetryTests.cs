// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.ForkChoice;
using Nethermind.Db;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.Sync;

/// <summary>
/// A finalized state is hundreds of megabytes over one TLS stream, and a dropped stream must not leave the execution layer
/// without a consensus driver, while a provider that will never answer, or an answer that cannot be anchored, must still stop.
/// </summary>
public class CheckpointSyncRetryTests
{
    private static readonly TimeSpan NoDelay = TimeSpan.FromMilliseconds(1);

    [Test]
    public async Task A_state_download_dropped_mid_transfer_is_retried_from_the_start_and_anchored()
    {
        await using FlakyCheckpointProvider provider = await FlakyCheckpointProvider.StartAsync(static n => n == 1 ? StateResponse.DropMidBody : StateResponse.Serve);
        LevelCapturingLogManager logs = new();
        BeaconChainStore store = NewStore();
        using CheckpointSync sync = NewSync(provider, store, logs);

        CheckpointAnchor anchor = await sync.RunAsync(CancellationToken.None);

        (string Level, string Text)[] retries = [.. logs.Lines.Where(static l => l.Text.Contains("failed on attempt"))];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(anchor.BlockRoot, Is.EqualTo(ForkCrossingChain.Instance.First.Root));
            Assert.That(provider.StateRequests, Is.EqualTo(2), "the whole state is requested again");
            Assert.That(store.TryGetAnchor(out _, out _), Is.True);
            Assert.That(retries, Has.Length.EqualTo(1));
            Assert.That(retries[0].Level, Is.EqualTo("Info"));
            Assert.That(retries[0].Text, Does.Not.Contain("Exception").And.Not.Contain("\n").And.Not.Contain(" at "), "one line with the cause, no stack");
        }
    }

    [Test]
    public async Task A_provider_that_keeps_dropping_the_download_is_asked_the_named_maximum_of_times_and_its_failure_is_thrown()
    {
        await using FlakyCheckpointProvider provider = await FlakyCheckpointProvider.StartAsync(static _ => StateResponse.DropMidBody);
        BeaconChainStore store = NewStore();
        using CheckpointSync sync = NewSync(provider, store, new LevelCapturingLogManager(), maxDownloadAttempts: 3);

        Assert.ThrowsAsync<IOException>(() => sync.RunAsync(CancellationToken.None));

        Assert.That(provider.StateRequests, Is.EqualTo(3));
        Assert.That(store.TryGetAnchor(out _, out _), Is.False);
    }

    [Test]
    public async Task A_server_error_is_retried()
    {
        await using FlakyCheckpointProvider provider = await FlakyCheckpointProvider.StartAsync(static n => n == 1 ? StateResponse.ServerError : StateResponse.Serve);
        using CheckpointSync sync = NewSync(provider, NewStore(), new LevelCapturingLogManager());

        await sync.RunAsync(CancellationToken.None);

        Assert.That(provider.StateRequests, Is.EqualTo(2));
    }

    [Test]
    public async Task A_missing_endpoint_is_not_asked_again()
    {
        await using FlakyCheckpointProvider provider = await FlakyCheckpointProvider.StartAsync(static _ => StateResponse.NotFound);
        using CheckpointSync sync = NewSync(provider, NewStore(), new LevelCapturingLogManager());

        HttpRequestException refusal = Assert.ThrowsAsync<HttpRequestException>(() => sync.RunAsync(CancellationToken.None))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(refusal.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(provider.StateRequests, Is.EqualTo(1), "a 404 does not change on a second ask");
        }
    }

    [Test]
    public async Task A_checkpoint_of_an_unsupported_fork_is_not_asked_for_again()
    {
        await using FlakyCheckpointProvider provider = await FlakyCheckpointProvider.StartAsync(static _ => StateResponse.Serve, consensusVersion: "heze");
        using CheckpointSync sync = NewSync(provider, NewStore(), new LevelCapturingLogManager());

        Assert.ThrowsAsync<NotSupportedException>(() => sync.RunAsync(CancellationToken.None));

        Assert.That(provider.StateRequests, Is.EqualTo(1));
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Stopping_ends_the_wait_between_attempts(CancellationToken token)
    {
        await using FlakyCheckpointProvider provider = await FlakyCheckpointProvider.StartAsync(static _ => StateResponse.DropMidBody);
        LevelCapturingLogManager logs = new();
        using CheckpointSync sync = NewSync(provider, NewStore(), logs, retryBaseDelay: TimeSpan.FromHours(1));
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task run = sync.RunAsync(stop.Token);
        while (!logs.Lines.Any(static l => l.Text.Contains("failed on attempt")))
        {
            await Task.Delay(10, token);
        }

        await stop.CancelAsync();

        Assert.ThrowsAsync<TaskCanceledException>(() => run);
    }

    [TestCase(typeof(IOException), null, true)]
    [TestCase(typeof(HttpRequestException), null, true)]
    [TestCase(typeof(HttpRequestException), HttpStatusCode.InternalServerError, true)]
    [TestCase(typeof(HttpRequestException), HttpStatusCode.NotImplemented, false, Description = "A refusal that repeats on every ask.")]
    [TestCase(typeof(HttpRequestException), HttpStatusCode.HttpVersionNotSupported, false)]
    [TestCase(typeof(HttpRequestException), HttpStatusCode.BadGateway, true)]
    [TestCase(typeof(HttpRequestException), HttpStatusCode.ServiceUnavailable, true)]
    [TestCase(typeof(HttpRequestException), HttpStatusCode.GatewayTimeout, true)]
    [TestCase(typeof(HttpRequestException), HttpStatusCode.RequestTimeout, true)]
    [TestCase(typeof(HttpRequestException), HttpStatusCode.TooManyRequests, true)]
    [TestCase(typeof(HttpRequestException), HttpStatusCode.NotFound, false)]
    [TestCase(typeof(HttpRequestException), HttpStatusCode.Unauthorized, false)]
    [TestCase(typeof(TaskCanceledException), null, true, Description = "The client's own timeout.")]
    [TestCase(typeof(NotSupportedException), null, false)]
    [TestCase(typeof(InvalidDataException), null, false)]
    [TestCase(typeof(InvalidOperationException), null, false)]
    public void Only_network_failures_are_transient(Type exceptionType, HttpStatusCode? status, bool transient)
    {
        Exception exception = exceptionType == typeof(HttpRequestException)
            ? new HttpRequestException("failed", null, status)
            : (Exception)Activator.CreateInstance(exceptionType, "failed")!;

        Assert.That(CheckpointSync.IsTransientDownloadFailure(exception, CancellationToken.None), Is.EqualTo(transient));
    }

    [Test]
    public void A_cancellation_requested_by_the_caller_is_not_transient()
    {
        using CancellationTokenSource stopped = new();
        stopped.Cancel();

        Assert.That(CheckpointSync.IsTransientDownloadFailure(new OperationCanceledException(stopped.Token), stopped.Token), Is.False);
    }

    [Test]
    public async Task An_anchor_block_dropped_mid_transfer_is_retried_without_asking_for_the_state_again()
    {
        await using FlakyCheckpointProvider provider = await FlakyCheckpointProvider.StartAsync(static _ => StateResponse.Serve,
            blockResponseFor: static n => n == 1 ? StateResponse.DropMidBody : StateResponse.Serve);
        LevelCapturingLogManager logs = new();
        using CheckpointSync sync = NewSync(provider, NewStore(), logs);

        CheckpointAnchor anchor = await sync.RunAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(anchor.Block, Is.Not.Null);
            Assert.That(provider.StateRequests, Is.EqualTo(1));
            Assert.That(provider.BlockRequests, Is.EqualTo(2));
            Assert.That(logs.Lines.Where(static l => l.Text.Contains("anchor block download failed on attempt 1")), Has.Exactly(1).Items);
        }
    }

    [Test]
    public void The_wait_between_attempts_doubles_and_stops_at_thirty_seconds()
    {
        TimeSpan delay = TimeSpan.FromSeconds(2);
        List<double> seconds = [];
        for (int i = 0; i < 6; i++)
        {
            seconds.Add(delay.TotalSeconds);
            delay = CheckpointSync.NextRetryDelay(delay);
        }

        Assert.That(seconds, Is.EqualTo(new[] { 2d, 4, 8, 16, 30, 30 }));
    }

    [Test]
    public async Task Each_wait_between_attempts_is_twice_the_one_before()
    {
        await using FlakyCheckpointProvider provider = await FlakyCheckpointProvider.StartAsync(static n => n <= 2 ? StateResponse.DropMidBody : StateResponse.Serve);
        LevelCapturingLogManager logs = new();
        using CheckpointSync sync = NewSync(provider, NewStore(), logs, retryBaseDelay: TimeSpan.FromSeconds(1));

        await sync.RunAsync(CancellationToken.None);

        string[] waits = [.. logs.Lines.Where(static l => l.Text.Contains("failed on attempt")).Select(static l => l.Text[l.Text.LastIndexOf("retrying in ", StringComparison.Ordinal)..])];
        Assert.That(waits, Is.EqualTo(new[] { "retrying in 1 s.", "retrying in 2 s." }));
    }

    [Test]
    public async Task The_buffer_of_a_dropped_download_goes_back_to_the_pool()
    {
        await using FlakyCheckpointProvider provider = await FlakyCheckpointProvider.StartAsync(static n => n == 1 ? StateResponse.DropMidBody : StateResponse.Serve);
        OutstandingArrayPool pool = new();
        using CheckpointSync sync = NewSync(provider, NewStore(), new LevelCapturingLogManager(), bufferPool: pool);

        await sync.RunAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pool.Rented, Is.GreaterThanOrEqualTo(2), "each attempt reads into the pool");
            Assert.That(pool.Outstanding, Is.Zero);
        }
    }

    [Test]
    public void The_cause_logged_is_the_innermost_message_without_a_trailing_period()
    {
        HttpRequestException wrapped = new("Error while copying content to a stream.", new IOException("Unable to read data from the transport connection: reset by peer."));

        Assert.That(CheckpointSync.DescribeCause(wrapped), Is.EqualTo("Unable to read data from the transport connection: reset by peer"));
    }

    private static BeaconChainStore NewStore() => new(new MemColumnsDb<BeaconChainDbColumns>(), GloasCheckpointFiles.Spec);

    private static CheckpointSync NewSync(FlakyCheckpointProvider provider, BeaconChainStore store, LevelCapturingLogManager logs, int maxDownloadAttempts = 5, TimeSpan? retryBaseDelay = null, ArrayPool<byte>? bufferPool = null) =>
        new(new BeaconChainConfig { CheckpointSyncUrl = provider.Url }, GloasCheckpointFiles.Spec, store, logs)
        {
            MaxDownloadAttempts = maxDownloadAttempts,
            RetryBaseDelay = retryBaseDelay ?? NoDelay,
            BufferPool = bufferPool ?? ArrayPool<byte>.Shared,
        };

    /// <summary>The shared pool, counting the arrays rented from it and not yet returned.</summary>
    private sealed class OutstandingArrayPool : ArrayPool<byte>
    {
        private readonly HashSet<byte[]> _outstanding = [];
        private readonly Lock _lock = new();
        private int _rented;

        public int Rented => Volatile.Read(ref _rented);

        public int Outstanding
        {
            get
            {
                lock (_lock)
                {
                    return _outstanding.Count;
                }
            }
        }

        public override byte[] Rent(int minimumLength)
        {
            byte[] array = Shared.Rent(minimumLength);
            Interlocked.Increment(ref _rented);
            lock (_lock)
            {
                _outstanding.Add(array);
            }

            return array;
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            lock (_lock)
            {
                _outstanding.Remove(array);
            }

            Shared.Return(array, clearArray);
        }
    }
}
