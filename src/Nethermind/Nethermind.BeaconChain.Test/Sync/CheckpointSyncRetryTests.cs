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
using Nethermind.BeaconChain.Types;
using Nethermind.Db;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.Sync;

public class CheckpointSyncRetryTests
{
    private static readonly TimeSpan NoDelay = TimeSpan.FromMilliseconds(1);

    /// <summary>Allow local response scheduling jitter while keeping stalled reads bounded.</summary>
    internal static readonly TimeSpan ReadStall = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan RunBound = TimeSpan.FromSeconds(60);

    public enum DownloadFailure
    {
        StateDrop,
        StateStall,
        AnchorDrop,
        AnchorStall,
        HeadersStall,
        ServerError,
        RepeatedDrop,
        MissingEndpoint,
        UnsupportedFork,
    }

    [Test]
    public async Task Checkpoint_download_retries_only_transient_failures_without_redownloading_completed_state([Values] DownloadFailure failure)
    {
        bool anchorFailure = failure is DownloadFailure.AnchorDrop or DownloadFailure.AnchorStall;
        StateResponse response = failure switch
        {
            DownloadFailure.StateStall or DownloadFailure.AnchorStall => StateResponse.StallMidBody,
            DownloadFailure.HeadersStall => StateResponse.NoHeaders,
            DownloadFailure.ServerError => StateResponse.ServerError,
            DownloadFailure.MissingEndpoint => StateResponse.NotFound,
            _ => StateResponse.DropMidBody,
        };
        StateResponse Reply(int attempt) => failure == DownloadFailure.UnsupportedFork ? StateResponse.Serve
            : (failure is DownloadFailure.RepeatedDrop or DownloadFailure.MissingEndpoint || attempt == 1) ? response : StateResponse.Serve;
        await using FlakyCheckpointProvider provider = await FlakyCheckpointProvider.StartAsync(
            anchorFailure ? static _ => StateResponse.Serve : Reply,
            blockResponseFor: anchorFailure ? Reply : null,
            consensusVersion: failure == DownloadFailure.UnsupportedFork ? "heze" : null);
        TestLogRecorder logs = new();
        BeaconChainStore store = NewStore();
        using CheckpointSync sync = NewSync(provider, store, logs,
            maxDownloadAttempts: failure == DownloadFailure.RepeatedDrop ? 3 : 5,
            responseHeadersTimeout: failure == DownloadFailure.HeadersStall ? TimeSpan.FromSeconds(2) : null);
        if (failure is DownloadFailure.RepeatedDrop or DownloadFailure.MissingEndpoint or DownloadFailure.UnsupportedFork)
        {
            if (failure == DownloadFailure.RepeatedDrop)
            {
                Assert.ThrowsAsync<IOException>(() => sync.RunAsync(CancellationToken.None));
                Assert.That(store.TryGetAnchor(out _, out _), Is.False);
            }
            else if (failure == DownloadFailure.MissingEndpoint)
            {
                HttpRequestException refusal = Assert.ThrowsAsync<HttpRequestException>(() => sync.RunAsync(CancellationToken.None))!;
                Assert.That(refusal.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            }
            else Assert.ThrowsAsync<NotSupportedException>(() => sync.RunAsync(CancellationToken.None));
            Assert.That(provider.StateRequests, Is.EqualTo(failure == DownloadFailure.RepeatedDrop ? 3 : 1));
            return;
        }
        Task<CheckpointAnchor> run = sync.RunAsync(CancellationToken.None);
        CheckpointAnchor anchor = failure == DownloadFailure.ServerError ? await run : await run.WaitAsync(RunBound);
        (string Level, string Text)[] retries = [.. logs.Lines.Where(static l => l.Text.Contains("failed on attempt"))];
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(provider.StateRequests, Is.EqualTo(anchorFailure ? 1 : 2));
        if (anchorFailure)
        {
            Assert.That(anchor.Block, Is.Not.Null);
            Assert.That(provider.BlockRequests, Is.EqualTo(2));
            Assert.That(logs.Lines.Where(static l => l.Text.Contains("anchor block download failed on attempt 1")), Has.Exactly(1).Items);
        }
        else if (failure != DownloadFailure.ServerError)
        {
            Assert.That(anchor.BlockRoot, Is.EqualTo(ForkCrossingChain.Instance.First.Root));
            Assert.That(retries, Has.Length.EqualTo(1));
            if (failure == DownloadFailure.HeadersStall)
                Assert.That(retries[0].Text, Is.EqualTo("Checkpoint state download failed on attempt 1 of 5: No response headers arrived for 2 s; retrying in 0 s."));
            else
            {
                Assert.That(store.TryGetAnchor(out _, out _), Is.True);
                Assert.That(retries[0].Level, Is.EqualTo("Info"));
                Assert.That(retries[0].Text, Does.Not.Contain("Exception").And.Not.Contain("\n").And.Not.Contain(" at "));
                if (failure == DownloadFailure.StateStall) Assert.That(retries[0].Text, Does.Contain("No data arrived for 10 s"));
            }
        }
    }

    [Test]
    public async Task A_state_arriving_for_longer_than_the_stall_timeout_but_never_pausing_that_long_is_not_cut()
    {
        await using FlakyCheckpointProvider provider = await FlakyCheckpointProvider.StartAsync(static _ => StateResponse.Trickle);
        TimeSpan bound = FlakyCheckpointProvider.TrickleDuration / 2;
        TestLogRecorder logs = new();
        // The headers bound is below the transfer time too, so it must not cover the body either.
        using CheckpointSync sync = NewSync(provider, NewStore(), logs, readStallTimeout: bound, responseHeadersTimeout: bound);

        CheckpointAnchor anchor = await sync.RunAsync(CancellationToken.None).WaitAsync(RunBound);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(anchor.BlockRoot, Is.EqualTo(ForkCrossingChain.Instance.First.Root));
        Assert.That(provider.StateRequests, Is.EqualTo(1), "a slow but steady state is read in one request");
        Assert.That(logs.Lines.Where(static l => l.Text.Contains("failed on attempt")), Is.Empty);
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Stopping_ends_the_wait_between_attempts(CancellationToken token)
    {
        await using FlakyCheckpointProvider provider = await FlakyCheckpointProvider.StartAsync(static _ => StateResponse.DropMidBody);
        TestLogRecorder logs = new();
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

    [Test]
    [CancelAfter(30_000)]
    public async Task Stopping_during_a_stalled_read_is_a_cancellation_and_not_a_drop(CancellationToken token)
    {
        await using FlakyCheckpointProvider provider = await FlakyCheckpointProvider.StartAsync(static _ => StateResponse.StallMidBody);
        TestLogRecorder logs = new();
        OutstandingArrayPool pool = new();
        using CheckpointSync sync = NewSync(provider, NewStore(), logs, bufferPool: pool, readStallTimeout: TimeSpan.FromHours(1));
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task run = sync.RunAsync(stop.Token);
        byte[] sent = await provider.StallEntered.WaitAsync(token);
        // Once all the sent bytes are in the buffer, the next read has nothing to return; the settle lets the loop issue and park it.
        while (pool.LastRented is not { } buffer || !buffer.AsSpan(0, sent.Length).SequenceEqual(sent))
        {
            await Task.Delay(10, token);
        }

        await Task.Delay(200, token);

        await stop.CancelAsync();

        await Assert.ThatAsync(() => run.WaitAsync(TimeSpan.FromSeconds(10), token), Throws.InstanceOf<OperationCanceledException>(), "the waiting read ends on the stop");
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(logs.Lines.Where(static l => l.Text.Contains("failed on attempt")), Is.Empty, "a stop is not retried");
        Assert.That(pool.Rented, Is.Positive);
        Assert.That(pool.Outstanding, Is.Zero, "a stopped read returns its buffer");
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
        TestLogRecorder logs = new();
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
        using CheckpointSync sync = NewSync(provider, NewStore(), new TestLogRecorder(), bufferPool: pool);

        await sync.RunAsync(CancellationToken.None);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(pool.Rented, Is.GreaterThanOrEqualTo(2), "each attempt reads into the pool");
        Assert.That(pool.Outstanding, Is.Zero);
    }

    [Test]
    public void The_cause_logged_is_the_innermost_message_without_a_trailing_period()
    {
        HttpRequestException wrapped = new("Error while copying content to a stream.", new IOException("Unable to read data from the transport connection: reset by peer."));

        Assert.That(CheckpointSync.DescribeCause(wrapped), Is.EqualTo("Unable to read data from the transport connection: reset by peer"));
    }

    private static BeaconChainStore NewStore() => new(new MemColumnsDb<BeaconChainDbColumns>(), GloasCheckpointFiles.Spec);

    [Test]
    public async Task A_body_below_the_minimum_rate_is_cut_and_one_above_it_is_not([Values] bool belowRate)
    {
        await using FlakyCheckpointProvider provider = await FlakyCheckpointProvider.StartAsync(static _ => StateResponse.Trickle);
        double trickleRate = BeaconStateGloas.Encode(ForkCrossingChain.Instance.First.PostState).Length / FlakyCheckpointProvider.TrickleDuration.TotalSeconds;
        OutstandingArrayPool pool = new();
        using CheckpointSync sync = new(new BeaconChainConfig { CheckpointSyncUrl = provider.Url }, GloasCheckpointFiles.Spec, NewStore(), new TestLogRecorder())
        {
            MaxDownloadAttempts = 1,
            ReadStallTimeout = ReadStall,
            MinThroughputBytesPerSecond = Math.Max(1, (int)(belowRate ? trickleRate * 8 : trickleRate / 8)),
            MinThroughputGrace = TimeSpan.FromSeconds(1),
            BufferPool = pool,
        };

        if (belowRate)
        {
            IOException refusal = Assert.ThrowsAsync<IOException>(() => sync.RunAsync(CancellationToken.None).WaitAsync(RunBound))!;
            Assert.That(refusal.Message, Does.Contain("KiB/s"));
        }
        else
        {
            CheckpointAnchor anchor = await sync.RunAsync(CancellationToken.None).WaitAsync(RunBound);
            Assert.That(anchor.BlockRoot, Is.EqualTo(ForkCrossingChain.Instance.First.Root));
        }

        Assert.That(pool.Outstanding, Is.Zero);
    }

    [Test]
    public async Task A_body_download_deadline_cancels_a_read_before_the_stall_timeout()
    {
        await using FlakyCheckpointProvider provider = await FlakyCheckpointProvider.StartAsync(static _ => StateResponse.StallMidBody);
        OutstandingArrayPool pool = new();
        using CheckpointSync sync = new(new BeaconChainConfig { CheckpointSyncUrl = provider.Url }, GloasCheckpointFiles.Spec, NewStore(), new TestLogRecorder())
        {
            MaxDownloadAttempts = 1,
            ReadStallTimeout = TimeSpan.FromHours(1),
            BodyDownloadTimeout = TimeSpan.FromSeconds(2),
            BufferPool = pool,
        };

        IOException refusal = Assert.ThrowsAsync<IOException>(() => sync.RunAsync(CancellationToken.None).WaitAsync(RunBound))!;
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(refusal.Message, Does.Contain("download deadline"));
        Assert.That(pool.Outstanding, Is.Zero);
    }

    [Test]
    public void An_oversized_local_state_is_refused_before_renting()
    {
        using GloasCheckpointFiles files = GloasCheckpointFiles.Write(ForkCrossingChain.Instance.First.PostState, null);
        OutstandingArrayPool pool = new();
        using CheckpointSync sync = new(new BeaconChainConfig { CheckpointStateFile = files.StateFile }, GloasCheckpointFiles.Spec, NewStore(), new TestLogRecorder())
        {
            MaxBodyBytes = 16,
            BufferPool = pool,
        };

        Assert.ThrowsAsync<InvalidDataException>(() => sync.RunAsync(CancellationToken.None));
        Assert.That(pool.Rented, Is.Zero);
    }

    [Test]
    public void An_oversized_declared_body_is_refused_before_renting([Values(17L, 2147483648L)] long declared)
    {
        OutstandingArrayPool pool = new();
        using CheckpointSync sync = new(new BeaconChainConfig(), GloasCheckpointFiles.Spec, NewStore(), new TestLogRecorder())
        {
            MaxBodyBytes = 16,
            BufferPool = pool,
        };
        using HttpResponseMessage response = new() { Content = new ByteArrayContent([]) };
        response.Content.Headers.ContentLength = declared;

        Assert.ThrowsAsync<InvalidDataException>(() => sync.ReadResponseBodyAsync(response, 8, CancellationToken.None));
        Assert.That(pool.Rented, Is.Zero);
    }

    [Test]
    public async Task Body_reads_respect_the_limit_even_when_the_pool_returns_a_larger_array([Values(17, 18)] int bytes)
    {
        OutstandingArrayPool pool = new();
        using CheckpointSync sync = new(new BeaconChainConfig(), GloasCheckpointFiles.Spec, NewStore(), new TestLogRecorder())
        {
            MaxBodyBytes = 17,
            BufferPool = pool,
        };
        using HttpResponseMessage response = new() { Content = new ByteArrayContent(new byte[bytes]) };
        response.Content.Headers.ContentLength = 1;

        if (bytes > 17)
        {
            Assert.ThrowsAsync<InvalidDataException>(() => sync.ReadResponseBodyAsync(response, 1, CancellationToken.None));
        }
        else
        {
            (byte[] buffer, int length) = await sync.ReadResponseBodyAsync(response, 1, CancellationToken.None);
            Assert.That(length, Is.EqualTo(17));
            pool.Return(buffer);
        }
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(pool.Outstanding, Is.Zero);
        Assert.That(pool.LargestRequest, Is.LessThanOrEqualTo(17));
    }

    private static CheckpointSync NewSync(FlakyCheckpointProvider provider, BeaconChainStore store, TestLogRecorder logs, int maxDownloadAttempts = 5, TimeSpan? retryBaseDelay = null, ArrayPool<byte>? bufferPool = null, TimeSpan? readStallTimeout = null, TimeSpan? responseHeadersTimeout = null) =>
        new(new BeaconChainConfig { CheckpointSyncUrl = provider.Url }, GloasCheckpointFiles.Spec, store, logs)
        {
            MaxDownloadAttempts = maxDownloadAttempts,
            RetryBaseDelay = retryBaseDelay ?? NoDelay,
            BufferPool = bufferPool ?? ArrayPool<byte>.Shared,
            ReadStallTimeout = readStallTimeout ?? ReadStall,
            ResponseHeadersTimeout = responseHeadersTimeout ?? ReadStall,
        };

    /// <summary>Clear rentals so written bytes remain observable; track every outstanding array.</summary>
    private sealed class OutstandingArrayPool : ArrayPool<byte>
    {
        private readonly HashSet<byte[]> _outstanding = [];
        private readonly Lock _lock = new();
        private int _rented;
        private byte[]? _lastRented;

        public int Rented => Volatile.Read(ref _rented);

        public int LargestRequest { get; private set; }

        public byte[]? LastRented => Volatile.Read(ref _lastRented);

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
            LargestRequest = Math.Max(LargestRequest, minimumLength);
            byte[] array = Shared.Rent(minimumLength);
            Array.Clear(array);
            Interlocked.Increment(ref _rented);
            Volatile.Write(ref _lastRented, array);
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
