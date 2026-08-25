// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Mime;
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
/// Each level gets a fresh connection pool and, unless warm-up is disabled, opens every connection
/// with a burst of simultaneous priming requests and then warms up on the records immediately before
/// the measured window. The measured window therefore neither pays connection setup nor replays
/// requests its own warm-up just answered.
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

    // "latest" is what the node substitutes for an omitted block parameter, so only then is a record
    // without the slot already at the requested block.
    private readonly bool _targetIsNodeDefault = string.Equals(options.BlockTag, "latest", StringComparison.Ordinal);

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
                Report($"concurrency {concurrency}: priming {concurrency} connections");
                await PrimeConnectionsAsync(concurrency, httpClient, auth, token);

                Report($"concurrency {concurrency}: warm-up, {_options.WarmupRequests} requests");
                LevelResult warmup = await RunPassAsync(concurrency, _options.WarmupRequests, _options.Skip, httpClient, auth, measure: false, token);
                if (warmup.Failed > 0)
                {
                    Warn($"concurrency {concurrency}: warm-up had {warmup.Failed}/{warmup.Total} failures; the measured window may be cold");
                }
            }

            Report($"concurrency {concurrency}: measuring");
            LevelResult result = await RunPassAsync(concurrency, RequestLimit, MeasuredSkip, httpClient, auth, measure: true, token);
            results.Add(result);

            if (result.Untagged > 0)
            {
                Warn($"concurrency {concurrency}: {result.Untagged}/{result.Total} requests were sent without the forced block tag (unknown method or absent block slot)");
            }

            Report($"concurrency {concurrency}: {result.Total} requests in {result.Elapsed.TotalSeconds:F1}s, "
                + $"{result.RequestsPerSecond:F1} rps, p50 {result.P50.TotalMilliseconds:F1}ms, "
                + $"p99 {result.P99.TotalMilliseconds:F1}ms, {result.Failed} failed");
        }

        return results;
    }

    private int RequestLimit => _options.MeasuredRequests <= 0 ? int.MaxValue : _options.MeasuredRequests;

    /// <summary>First record of the measured window; the warm-up consumes the records before it.</summary>
    private int MeasuredSkip => _options.Skip + (_options.WarmupRequests > 0 ? _options.WarmupRequests : 0);

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
        int skip,
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
            WorkerTally tally = new(perWorkerHint);
            tallies[i] = tally;
            RawJsonRpcClient client = new(httpClient, _options.Address, auth);
            tasks[i + 1] = Task.Run(
                () => WorkerAsync(channel.Reader, client, tally, deadline, passCts, passToken),
                CancellationToken.None);
        }

        tasks[0] = Task.Run(
            () => ReadAsync(channel.Writer, requestLimit, skip, deadline, passToken),
            CancellationToken.None);

        await Task.WhenAll(tasks);

        return Aggregate(concurrency, tallies);
    }

    /// <summary>Decompresses the trace, applies the replay edits and feeds the workers.</summary>
    private async Task ReadAsync(ChannelWriter<PendingRequest> writer, int requestLimit, int skip, PassDeadline deadline, CancellationToken token)
    {
        try
        {
            using TraceLineReader reader = new(_options.InputPath);
            SkipLeadingRecords(reader, skip);

            int sent = 0;
            while (sent < requestLimit && !deadline.HasExpired && reader.TryReadRecord(out ReadOnlySpan<byte> record))
            {
                await writer.WriteAsync(Materialize(record, reader.RecordsRead), token);
                sent++;
            }
        }
        finally
        {
            writer.Complete();
        }
    }

    private static void SkipLeadingRecords(TraceLineReader reader, int count)
    {
        for (int skipped = 0; skipped < count; skipped++)
        {
            if (!reader.TryReadRecord(out _))
            {
                return;
            }
        }
    }

    /// <summary>Opens every connection the level will use by sending one request per connection at once.</summary>
    /// <remarks>
    /// The pool assigns a connection before it serializes a request body, so bodies that wait for the
    /// whole burst keep one connection occupied each until all of them exist. Merely starting the
    /// sends together is not enough: a fast early response can hand its connection to a later send
    /// instead of the pool opening a fresh one. Feeding warm-up requests through the shared channel
    /// cannot guarantee coverage either, since a fast worker can take two while another takes none.
    /// </remarks>
    private async Task PrimeConnectionsAsync(int concurrency, HttpClient httpClient, IAuth? auth, CancellationToken token)
    {
        using TraceLineReader reader = new(_options.InputPath);
        SkipLeadingRecords(reader, _options.Skip);
        if (!reader.TryReadRecord(out ReadOnlySpan<byte> record))
        {
            return;
        }

        PendingRequest request = Materialize(record, reader.RecordsRead);
        ReadOnlyMemory<byte> body = request.Buffer.AsMemory(0, request.Length);
        GatedContent.Gate gate = new(concurrency);

        Task<bool>[] primes = new Task<bool>[concurrency];
        for (int i = 0; i < concurrency; i++)
        {
            primes[i] = PrimeOneAsync(httpClient, auth, body, gate, token);
        }

        await Task.WhenAll(primes);
        ArrayPool<byte>.Shared.Return(request.Buffer);

        int failed = 0;
        foreach (Task<bool> prime in primes)
        {
            if (!prime.Result)
            {
                failed++;
            }
        }

        if (failed > 0)
        {
            Warn($"concurrency {concurrency}: priming failed {failed}/{concurrency} requests; their connections may open inside the measured window");
        }
    }

    /// <summary>Sends one priming request, reporting whether it got a success status.</summary>
    private async Task<bool> PrimeOneAsync(HttpClient httpClient, IAuth? auth, ReadOnlyMemory<byte> body, GatedContent.Gate gate, CancellationToken token)
    {
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Post, _options.Address) { Content = new GatedContent(body, gate) };
            if (auth is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AuthToken);
            }

            using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            await response.Content.CopyToAsync(Stream.Null, token);

            return response.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return false;
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
    private PendingRequest Materialize(ReadOnlySpan<byte> record, long recordNumber)
    {
        bool forceBlock = _quotedTag.Length > 0;
        bool untagged = false;
        if (forceBlock || _options.StripFeeFields)
        {
            if (RequestRewriter.IsBatch(record))
            {
                // Entries carry their own block and fee fields the planner cannot reach; sending the
                // batch as captured would silently break the rewrite contract.
                throw new InvalidDataException(
                    $"Record {recordNumber} is a batch, whose entries cannot be retagged or stripped. Replay batches with '-b keep --keep-fees'.");
            }

            Span<RequestEdit> planned = stackalloc RequestEdit[RequestRewriter.MaxEdits];
            int count = RequestRewriter.Plan(record, forceBlock, _options.StripFeeFields, planned);
            untagged = forceBlock && IsUntagged(record, planned[..count]);
            ReadOnlySpan<RequestEdit> edits = Keep(record, planned[..count], out bool rewroteBlock, out bool droppedFees);

            if (!edits.IsEmpty)
            {
                int size = RequestRewriter.RewrittenLength(record, edits, _quotedTag);
                byte[] patched = ArrayPool<byte>.Shared.Rent(size);
                int written = RequestRewriter.Apply(record, edits, _quotedTag, patched);

                return new PendingRequest(patched, written, rewroteBlock, droppedFees, untagged);
            }
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(record.Length);
        record.CopyTo(buffer);

        return new PendingRequest(buffer, record.Length, RewroteBlock: false, StrippedFees: false, untagged);
    }

    /// <summary>Whether the forced tag failed to apply because no block edit could be planned.</summary>
    /// <remarks>
    /// No planned edit means the slot was never reached: the method's position is unknown, or
    /// <c>params</c> stops before it. The latter is clean only when the forced tag is the node's own
    /// default for an omitted parameter.
    /// </remarks>
    private bool IsUntagged(ReadOnlySpan<byte> record, ReadOnlySpan<RequestEdit> planned)
    {
        foreach (RequestEdit edit in planned)
        {
            if (edit.IsBlockParameter)
            {
                return false;
            }
        }

        return !_targetIsNodeDefault
            || RequestRewriter.LocateBlockParameter(record, out _, out _) == BlockParameterPresence.UnknownMethod;
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

                tally.Add(start, end, request.Length, outcome, request.RewroteBlock, request.StrippedFees, request.Untagged);
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
        SkipLeadingRecords(reader, _options.Skip);

        int limit = RequestLimit;
        while (records < limit && reader.TryReadRecord(out ReadOnlySpan<byte> record))
        {
            token.ThrowIfCancellationRequested();

            PendingRequest request = Materialize(record, reader.RecordsRead);
            string? failure = Validate(request, reader.RecordsRead);

            // Returned before the deliberate throw below rather than abandoned to the GC.
            ArrayPool<byte>.Shared.Return(request.Buffer);
            if (failure is not null)
            {
                throw new InvalidDataException(failure);
            }

            if (request.RewroteBlock)
            {
                rewritten++;
            }

            if (request.StrippedFees)
            {
                feesStripped++;
            }

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
            Untagged = 0,
        };
    }

    /// <summary>Describes why a record left the rewriter still needing an edit; <see langword="null"/> when clean.</summary>
    private string? Validate(PendingRequest request, long recordNumber)
    {
        ReadOnlySpan<byte> body = request.Buffer.AsSpan(0, request.Length);

        if (_quotedTag.Length > 0)
        {
            switch (RequestRewriter.LocateBlockParameter(body, out int start, out int length))
            {
                case BlockParameterPresence.UnknownMethod:
                    return $"Record {recordNumber} cannot be retagged: its method has no known block position, or the record is malformed.";
                case BlockParameterPresence.Absent when !_targetIsNodeDefault:
                    return $"Record {recordNumber} omits its block parameter, which the node defaults to latest rather than {_options.BlockTag}.";
                case BlockParameterPresence.Present when !body.Slice(start, length).SequenceEqual(_quotedTag):
                    {
                        string actual = Encoding.UTF8.GetString(body.Slice(start, length));
                        return $"Record {recordNumber} still carries block parameter {actual}.";
                    }
            }
        }

        if (_options.StripFeeFields && RequestRewriter.HasFeeField(body))
        {
            return $"Record {recordNumber} still carries a fee field.";
        }

        return null;
    }

    private static LevelResult Aggregate(int concurrency, WorkerTally[] tallies)
    {
        int samples = 0;
        long firstStart = long.MaxValue;
        long lastEnd = long.MinValue;
        foreach (WorkerTally tally in tallies)
        {
            samples += tally.LatencyTimestamps.Count;
            firstStart = Math.Min(firstStart, tally.FirstStart);
            lastEnd = Math.Max(lastEnd, tally.LastEnd);
        }

        // The window covers every request, including transport errors that produced no latency sample.
        TimeSpan elapsed = lastEnd != long.MinValue ? Stopwatch.GetElapsedTime(firstStart, lastEnd) : TimeSpan.Zero;

        long[] timestamps = new long[samples];
        int succeeded = 0;
        int rpcErrors = 0;
        int httpErrors = 0;
        int transportErrors = 0;
        int rewritten = 0;
        int feesStripped = 0;
        int untagged = 0;
        long requestBytes = 0;
        int offset = 0;

        foreach (WorkerTally tally in tallies)
        {
            IReadOnlyList<long> workerLatencies = tally.LatencyTimestamps;
            for (int i = 0; i < workerLatencies.Count; i++)
            {
                timestamps[offset++] = workerLatencies[i];
            }

            succeeded += tally.Succeeded;
            rpcErrors += tally.RpcErrors;
            httpErrors += tally.HttpErrors;
            transportErrors += tally.TransportErrors;
            rewritten += tally.Rewritten;
            feesStripped += tally.FeesStripped;
            untagged += tally.Untagged;
            requestBytes += tally.RequestBytes;
        }

        Array.Sort(timestamps);
        TimeSpan[] latencies = new TimeSpan[samples];
        for (int i = 0; i < samples; i++)
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
            Rewritten = rewritten,
            FeesStripped = feesStripped,
            Untagged = untagged,
        };
    }

    private void Report(string message)
    {
        if (_options.Progress)
        {
            Warn(message);
        }
    }

    /// <summary>Writes a line regardless of the progress setting: it changes how to read the results.</summary>
    private void Warn(string message)
    {
        _log.WriteLine(message);
        _log.Flush();
    }

    private static string JsonQuote(string value) => string.Concat("\"", value, "\"");

    /// <summary>A request body waiting to be sent, in a pooled buffer owned by whichever worker takes it.</summary>
    /// <remarks>
    /// Carries its edit flags so they are tallied only when it is actually sent: a request dropped at
    /// the deadline must not be reported as an edited request.
    /// </remarks>
    private readonly record struct PendingRequest(byte[] Buffer, int Length, bool RewroteBlock, bool StrippedFees, bool Untagged);

    /// <summary>A priming request body that starts writing only once the whole burst holds connections.</summary>
    /// <remarks>
    /// Serialization runs on the connection assigned to the request, so a body that waits for its
    /// peers keeps that connection occupied until the burst has one each. The wait gives up after a
    /// few seconds, degrading to a plain burst, so an endpoint that caps connections below the level
    /// stalls priming instead of hanging it.
    /// </remarks>
    // Internal rather than private so the gate's cancellation contract stays regression-tested.
    internal sealed class GatedContent : HttpContent
    {
        private readonly ReadOnlyMemory<byte> _body;
        private readonly Gate _gate;

        public GatedContent(ReadOnlyMemory<byte> body, Gate gate)
        {
            _body = body;
            _gate = gate;
            Headers.ContentType = new MediaTypeHeaderValue(MediaTypeNames.Application.Json) { CharSet = "utf-8" };
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            await _gate.WaitForBurstAsync(cancellationToken);
            await stream.WriteAsync(_body, cancellationToken);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _body.Length;
            return true;
        }

        /// <summary>Releases every waiting body once <paramref name="participants"/> of them have arrived.</summary>
        /// <param name="participants">Number of bodies that must be serializing before any writes.</param>
        public sealed class Gate(int participants)
        {
            private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(5);

            private readonly TaskCompletionSource _burstSerializing = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _remaining = participants;

            public async Task WaitForBurstAsync(CancellationToken token)
            {
                if (Interlocked.Decrement(ref _remaining) <= 0)
                {
                    _burstSerializing.TrySetResult();
                }

                try
                {
                    await _burstSerializing.Task.WaitAsync(s_timeout, token);
                }
                catch (TimeoutException)
                {
                    // Fewer connections available than asked for; release the rest and prime what exists.
                    _burstSerializing.TrySetResult();
                }
            }
        }
    }

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
