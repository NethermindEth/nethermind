// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Threading.Channels;
using Nethermind.Tools.Kute.Auth;

namespace Nethermind.Tools.Kute.Replay;

/// <summary>
/// Replays a captured JSON-RPC trace at a series of concurrency levels and reports the latency
/// distribution of each level.
/// </summary>
/// <remarks>
/// Every level replays the same prefix of the trace, so levels differ only in offered load. A level
/// runs as a bounded producer-consumer pipeline: one reader decompresses and rewrites records while
/// exactly <c>concurrency</c> workers keep that many requests in flight. Workers are persistent
/// rather than one task per request, so the number in flight is the number asked for and the harness
/// adds no scheduling noise of its own.
/// <para>
/// Each level gets a fresh connection pool and runs its warm-up pass on that pool, so the measured
/// window never pays connection setup.
/// </para>
/// </remarks>
/// <param name="options">Settings for the run.</param>
/// <param name="log">Receives progress lines; typically standard error.</param>
public sealed class ReplaySweep(ReplayOptions options, TextWriter log)
{
    private readonly ReplayOptions _options = options;
    private readonly TextWriter _log = log;
    private readonly byte[] _quotedTag = options.BlockTag is null
        ? []
        : Encoding.UTF8.GetBytes(JsonQuote(options.BlockTag));

    /// <summary>Runs every configured concurrency level in order.</summary>
    /// <param name="token">Cancels the run.</param>
    /// <returns>One result per level, in the order the levels ran.</returns>
    public async Task<IReadOnlyList<LevelResult>> RunAsync(CancellationToken token)
    {
        if (_options.DryRun)
        {
            return [DryRun(token)];
        }

        IAuth? auth = ReplayAuth.TryCreate(_options.SecretPath);
        List<LevelResult> results = new(_options.Concurrencies.Count);

        foreach (int concurrency in _options.Concurrencies)
        {
            using HttpClient httpClient = CreateHttpClient(concurrency);

            if (_options.WarmupRequests > 0)
            {
                // Every worker needs at least one request, or its connection opens inside the measured
                // window and the level pays a handshake it is supposed to have already paid.
                int warmup = Math.Max(_options.WarmupRequests, concurrency);
                if (warmup != _options.WarmupRequests)
                {
                    Report($"concurrency {concurrency}: warm-up raised to {warmup} to cover every connection");
                }

                Report($"concurrency {concurrency}: warm-up, {warmup} requests");
                await RunPassAsync(concurrency, warmup, httpClient, auth, measure: false, token);
            }

            Report($"concurrency {concurrency}: measuring");
            LevelResult result = await RunPassAsync(concurrency, RequestLimit, httpClient, auth, measure: true, token);
            results.Add(result);

            Report($"concurrency {concurrency}: {result.Total} requests in {result.Elapsed.TotalSeconds:F1}s, "
                + $"{result.RequestsPerSecond:F1} rps, p50 {result.P50.TotalMilliseconds:F1}ms, "
                + $"p99 {result.P99.TotalMilliseconds:F1}ms, {result.Failed} failed");
        }

        return results;
    }

    private int RequestLimit => _options.MeasuredRequests <= 0 ? int.MaxValue : _options.MeasuredRequests;

    private HttpClient CreateHttpClient(int concurrency)
    {
        SocketsHttpHandler handler = new()
        {
            // Proxy auto-detection costs seconds on Windows and would land inside the measured window.
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None,
            MaxConnectionsPerServer = concurrency,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(10),
        };

        return new HttpClient(handler) { Timeout = _options.Timeout };
    }

    private async Task<LevelResult> RunPassAsync(
        int concurrency,
        int requestLimit,
        HttpClient httpClient,
        IAuth? auth,
        bool measure,
        CancellationToken token)
    {
        // A worker fault must unblock the reader, which would otherwise wait forever on a full channel.
        using CancellationTokenSource passCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        CancellationToken passToken = passCts.Token;

        Channel<PendingRequest> channel = Channel.CreateBounded<PendingRequest>(
            new BoundedChannelOptions(Math.Max(concurrency * 2, 4))
            {
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
            });

        WorkerTally[] tallies = new WorkerTally[concurrency];
        Task[] tasks = new Task[concurrency + 1];
        int perWorkerHint = requestLimit == int.MaxValue ? 1024 : requestLimit / concurrency + 1;
        PassDeadline deadline = new(measure ? _options.MaxDuration : null);

        for (int i = 0; i < concurrency; i++)
        {
            WorkerTally tally = new(measure ? perWorkerHint : 0);
            tallies[i] = tally;
            RawJsonRpcClient client = new(httpClient, _options.Address, auth);
            tasks[i + 1] = Task.Run(
                () => WorkerAsync(channel.Reader, client, tally, measure, deadline, passCts, passToken),
                CancellationToken.None);
        }

        (int Rewritten, int FeesStripped) edited = default;
        tasks[0] = Task.Run(
            async () => edited = await ReadAsync(channel.Writer, requestLimit, deadline, passToken),
            CancellationToken.None);

        await Task.WhenAll(tasks);

        return Aggregate(concurrency, edited, tallies);
    }

    /// <summary>Decompresses the trace, applies the replay edits and feeds the workers.</summary>
    /// <returns>How many records had their block parameter rewritten, and how many lost a fee field.</returns>
    private async Task<(int Rewritten, int FeesStripped)> ReadAsync(ChannelWriter<PendingRequest> writer, int requestLimit, PassDeadline deadline, CancellationToken token)
    {
        int rewritten = 0;
        int feesStripped = 0;
        try
        {
            using TraceLineReader reader = new(_options.InputPath);
            SkipLeadingRecords(reader);

            int sent = 0;
            while (sent < requestLimit && !deadline.HasExpired && reader.TryReadRecord(out ReadOnlySpan<byte> record))
            {
                PendingRequest request = Materialize(record, ref rewritten, ref feesStripped);
                await writer.WriteAsync(request, token);
                sent++;
            }
        }
        finally
        {
            writer.Complete();
        }

        return (rewritten, feesStripped);
    }

    private void SkipLeadingRecords(TraceLineReader reader)
    {
        for (int skipped = 0; skipped < _options.Skip; skipped++)
        {
            if (!reader.TryReadRecord(out _))
            {
                return;
            }
        }
    }

    /// <summary>
    /// Copies a record into a pooled buffer, forcing the configured block parameter and dropping fee
    /// fields.
    /// </summary>
    /// <remarks>
    /// A block parameter that already matches the target is not an edit, so a record needing no change
    /// is copied straight through rather than rebuilt.
    /// </remarks>
    private PendingRequest Materialize(ReadOnlySpan<byte> record, ref int rewritten, ref int feesStripped)
    {
        bool forceBlock = _quotedTag.Length > 0;
        if (forceBlock || _options.StripFeeFields)
        {
            Span<RequestEdit> planned = stackalloc RequestEdit[RequestRewriter.MaxEdits];
            int count = RequestRewriter.Plan(record, forceBlock, _options.StripFeeFields, planned);
            ReadOnlySpan<RequestEdit> edits = Keep(record, planned[..count], out bool rewroteBlock, out bool droppedFees);

            if (!edits.IsEmpty)
            {
                int size = RequestRewriter.RewrittenLength(record, edits, _quotedTag);
                byte[] patched = ArrayPool<byte>.Shared.Rent(size);
                int written = RequestRewriter.Apply(record, edits, _quotedTag, patched);

                if (rewroteBlock)
                {
                    rewritten++;
                }

                if (droppedFees)
                {
                    feesStripped++;
                }

                return new PendingRequest(patched, written);
            }
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(record.Length);
        record.CopyTo(buffer);

        return new PendingRequest(buffer, record.Length);
    }

    /// <summary>Drops a block-parameter edit that would rewrite the tag to what it already says.</summary>
    private ReadOnlySpan<RequestEdit> Keep(ReadOnlySpan<byte> record, Span<RequestEdit> planned, out bool rewroteBlock, out bool droppedFees)
    {
        rewroteBlock = false;
        droppedFees = false;

        int kept = 0;
        for (int i = 0; i < planned.Length; i++)
        {
            RequestEdit edit = planned[i];
            if (edit.IsBlockParameter)
            {
                if (record.Slice(edit.Start, edit.Length).SequenceEqual(_quotedTag))
                {
                    continue;
                }

                rewroteBlock = true;
            }
            else
            {
                droppedFees = true;
            }

            planned[kept++] = edit;
        }

        return planned[..kept];
    }

    private static async Task WorkerAsync(
        ChannelReader<PendingRequest> reader,
        RawJsonRpcClient client,
        WorkerTally tally,
        bool measure,
        PassDeadline deadline,
        CancellationTokenSource passCts,
        CancellationToken token)
    {
        try
        {
            await foreach (PendingRequest request in reader.ReadAllAsync(token))
            {
                if (deadline.HasExpired)
                {
                    // Past the cap: drain what the reader already queued instead of sending it.
                    ArrayPool<byte>.Shared.Return(request.Buffer);
                    continue;
                }

                deadline.Start();

                long start = Stopwatch.GetTimestamp();
                RequestOutcome outcome = await client.SendAsync(request.Buffer.AsMemory(0, request.Length), token);
                long end = Stopwatch.GetTimestamp();

                ArrayPool<byte>.Shared.Return(request.Buffer);

                if (measure)
                {
                    tally.Add(start, end, request.Length, outcome);
                }
            }
        }
        catch
        {
            // Release the reader, which is otherwise blocked writing to a channel nobody drains.
            await passCts.CancelAsync();
            throw;
        }
    }

    private LevelResult DryRun(CancellationToken token)
    {
        long startTimestamp = Stopwatch.GetTimestamp();
        int rewritten = 0;
        int feesStripped = 0;
        int records = 0;
        long bytes = 0;

        using TraceLineReader reader = new(_options.InputPath);
        SkipLeadingRecords(reader);

        int limit = RequestLimit;
        while (records < limit && reader.TryReadRecord(out ReadOnlySpan<byte> record))
        {
            token.ThrowIfCancellationRequested();

            PendingRequest request = Materialize(record, ref rewritten, ref feesStripped);
            Verify(request, reader.RecordsRead);

            ArrayPool<byte>.Shared.Return(request.Buffer);
            bytes += request.Length;
            records++;
        }

        TimeSpan elapsed = Stopwatch.GetElapsedTime(startTimestamp);
        double seconds = Math.Max(elapsed.TotalSeconds, 1e-9);
        Report($"dry run: {records} records, {bytes / (double)(1L << 30):F2} GiB, {rewritten} retagged, "
            + $"{feesStripped} de-feed, {elapsed.TotalSeconds:F2}s, {bytes / (1L << 20) / seconds:F0} MiB/s");

        return new LevelResult
        {
            Concurrency = 0,
            Succeeded = records,
            RpcErrors = 0,
            HttpErrors = 0,
            TransportErrors = 0,
            Elapsed = elapsed,
            RequestBytes = bytes,
            Latencies = [],
            Rewritten = rewritten,
            FeesStripped = feesStripped,
        };
    }

    /// <summary>Fails the dry run if a record left the rewriter still needing an edit.</summary>
    private void Verify(PendingRequest request, long recordNumber)
    {
        ReadOnlySpan<byte> body = request.Buffer.AsSpan(0, request.Length);

        if (_quotedTag.Length > 0)
        {
            if (!RequestRewriter.TryLocateBlockParameter(body, out int start, out int length))
            {
                throw new InvalidDataException($"Record {recordNumber} has no block parameter to rewrite.");
            }

            if (!body.Slice(start, length).SequenceEqual(_quotedTag))
            {
                string actual = Encoding.UTF8.GetString(body.Slice(start, length));
                throw new InvalidDataException($"Record {recordNumber} still carries block parameter {actual}.");
            }
        }

        if (_options.StripFeeFields && RequestRewriter.HasFeeField(body))
        {
            throw new InvalidDataException($"Record {recordNumber} still carries a fee field.");
        }
    }

    private static LevelResult Aggregate(int concurrency, (int Rewritten, int FeesStripped) edited, WorkerTally[] tallies)
    {
        int total = 0;
        long firstStart = long.MaxValue;
        long lastEnd = long.MinValue;
        foreach (WorkerTally tally in tallies)
        {
            total += tally.LatencyTimestamps.Count;
            firstStart = Math.Min(firstStart, tally.FirstStart);
            lastEnd = Math.Max(lastEnd, tally.LastEnd);
        }

        TimeSpan elapsed = total > 0 ? Stopwatch.GetElapsedTime(firstStart, lastEnd) : TimeSpan.Zero;

        long[] timestamps = new long[total];
        int succeeded = 0;
        int rpcErrors = 0;
        int httpErrors = 0;
        int transportErrors = 0;
        long requestBytes = 0;
        int offset = 0;

        foreach (WorkerTally tally in tallies)
        {
            IReadOnlyList<long> samples = tally.LatencyTimestamps;
            for (int i = 0; i < samples.Count; i++)
            {
                timestamps[offset++] = samples[i];
            }

            succeeded += tally.Succeeded;
            rpcErrors += tally.RpcErrors;
            httpErrors += tally.HttpErrors;
            transportErrors += tally.TransportErrors;
            requestBytes += tally.RequestBytes;
        }

        Array.Sort(timestamps);
        TimeSpan[] latencies = new TimeSpan[total];
        for (int i = 0; i < total; i++)
        {
            latencies[i] = Stopwatch.GetElapsedTime(0, timestamps[i]);
        }

        return new LevelResult
        {
            Concurrency = concurrency,
            Succeeded = succeeded,
            RpcErrors = rpcErrors,
            HttpErrors = httpErrors,
            TransportErrors = transportErrors,
            Elapsed = elapsed,
            RequestBytes = requestBytes,
            Latencies = latencies,
            Rewritten = edited.Rewritten,
            FeesStripped = edited.FeesStripped,
        };
    }

    private void Report(string message)
    {
        if (_options.Progress)
        {
            _log.WriteLine(message);
            _log.Flush();
        }
    }

    private static string JsonQuote(string value) => string.Concat("\"", value, "\"");

    /// <summary>A request body waiting to be sent, in a pooled buffer owned by whichever worker takes it.</summary>
    private readonly record struct PendingRequest(byte[] Buffer, int Length);

    /// <summary>Bounds how long a level spends sending, measured from its first request.</summary>
    /// <remarks>
    /// The clock starts on the first send rather than when the pass is set up, so decompressing a
    /// <see cref="ReplayOptions.Skip"/> prefix does not consume the budget. Both the reader and the
    /// workers consult it, so expiry stops sending straight away instead of only stopping enqueueing:
    /// the channel holds twice the concurrency, which at high concurrency is a large overshoot.
    /// </remarks>
    /// <param name="limit">Wall-clock budget, or <see langword="null"/> for no cap.</param>
    private sealed class PassDeadline(TimeSpan? limit)
    {
        private readonly long _budget = limit is { } cap ? (long)(cap.TotalSeconds * Stopwatch.Frequency) : 0L;
        private long _expiresAt;

        /// <summary>Starts the clock, if this is the first call and a cap was set.</summary>
        public void Start()
        {
            if (_budget != 0L)
            {
                Interlocked.CompareExchange(ref _expiresAt, Stopwatch.GetTimestamp() + _budget, 0L);
            }
        }

        /// <summary>Whether the budget is set, started, and used up.</summary>
        public bool HasExpired
        {
            get
            {
                if (_budget == 0L)
                {
                    return false;
                }

                long expiresAt = Volatile.Read(ref _expiresAt);

                return expiresAt != 0L && Stopwatch.GetTimestamp() >= expiresAt;
            }
        }
    }
}
