// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;

namespace Nethermind.Tools.Kute.Replay;

/// <summary>How a single replayed request ended.</summary>
public enum RequestOutcome
{
    /// <summary>A 2xx response carrying a JSON-RPC result.</summary>
    Success,

    /// <summary>A 2xx response carrying a JSON-RPC <c>error</c> member.</summary>
    RpcError,

    /// <summary>A response with a non-success status code.</summary>
    HttpError,

    /// <summary>The request failed or timed out before a response was read.</summary>
    TransportError,
}

/// <summary>Outcome of replaying a trace at one concurrency level.</summary>
public sealed record LevelResult
{
    /// <summary>Number of requests kept in flight during the measured window.</summary>
    public required int Concurrency { get; init; }

    /// <summary>Requests that returned a JSON-RPC result.</summary>
    public required int Succeeded { get; init; }

    /// <summary>Requests answered with a JSON-RPC <c>error</c> member.</summary>
    public required int RpcErrors { get; init; }

    /// <summary>Requests answered with a non-success HTTP status.</summary>
    public required int HttpErrors { get; init; }

    /// <summary>Requests that failed or timed out before a response was read.</summary>
    public required int TransportErrors { get; init; }

    /// <summary>
    /// Wall-clock span from the first measured request being sent to the last one completing.
    /// </summary>
    /// <remarks>
    /// Measured across the requests themselves rather than the whole pass, so decompressing a
    /// <see cref="ReplayOptions.Skip"/> prefix does not count as time the node was under load.
    /// </remarks>
    public required TimeSpan Elapsed { get; init; }

    /// <summary>Total bytes of request bodies sent during the measured window.</summary>
    public required long RequestBytes { get; init; }

    /// <summary>Per-request latencies, sorted ascending.</summary>
    public required IReadOnlyList<TimeSpan> Latencies { get; init; }

    /// <summary>Requests whose block parameter was rewritten before being sent.</summary>
    public required int Rewritten { get; init; }

    /// <summary>Requests that failed for any reason.</summary>
    public int Failed => RpcErrors + HttpErrors + TransportErrors;

    /// <summary>Requests completed, successfully or not.</summary>
    public int Total => Succeeded + Failed;

    /// <summary>Requests completed per second, counting both successes and failures.</summary>
    public double RequestsPerSecond => Elapsed > TimeSpan.Zero ? Total / Elapsed.TotalSeconds : 0d;

    /// <summary>Share of requests that failed, in the range 0 to 1.</summary>
    public double FailureRate => Total > 0 ? (double)Failed / Total : 0d;

    /// <summary>Fastest measured request.</summary>
    public TimeSpan Min => Percentile(0d);

    /// <summary>Slowest measured request.</summary>
    public TimeSpan Max => Percentile(1d);

    /// <summary>Median latency.</summary>
    public TimeSpan P50 => Percentile(0.50d);

    /// <summary>90th percentile latency.</summary>
    public TimeSpan P90 => Percentile(0.90d);

    /// <summary>99th percentile latency. Needs a few thousand samples to be stable.</summary>
    public TimeSpan P99 => Percentile(0.99d);

    /// <summary>Arithmetic mean latency.</summary>
    public TimeSpan Mean
    {
        get
        {
            if (Latencies.Count == 0)
            {
                return TimeSpan.Zero;
            }

            long ticks = 0;
            for (int i = 0; i < Latencies.Count; i++)
            {
                ticks += Latencies[i].Ticks;
            }

            return TimeSpan.FromTicks(ticks / Latencies.Count);
        }
    }

    /// <summary>Nearest-rank percentile over the sorted latencies.</summary>
    /// <param name="quantile">Quantile in the range 0 to 1.</param>
    public TimeSpan Percentile(double quantile)
    {
        if (Latencies.Count == 0)
        {
            return TimeSpan.Zero;
        }

        int rank = (int)Math.Ceiling(quantile * Latencies.Count) - 1;
        return Latencies[Math.Clamp(rank, 0, Latencies.Count - 1)];
    }
}

/// <summary>Collects request outcomes for one worker, so workers never contend on a shared tally.</summary>
/// <remarks>
/// Latencies are kept as raw <see cref="Stopwatch"/> timestamp deltas and converted once the level
/// ends, keeping <see cref="TimeSpan"/> arithmetic out of the measured path.
/// </remarks>
/// <param name="expectedRequests">Hint used to size the latency buffer up front.</param>
public sealed class WorkerTally(int expectedRequests)
{
    private readonly List<long> _latencyTimestamps = new(Math.Max(expectedRequests, 4));

    /// <summary>Requests that returned a JSON-RPC result.</summary>
    public int Succeeded { get; private set; }

    /// <summary>Requests answered with a JSON-RPC <c>error</c> member.</summary>
    public int RpcErrors { get; private set; }

    /// <summary>Requests answered with a non-success HTTP status.</summary>
    public int HttpErrors { get; private set; }

    /// <summary>Requests that failed or timed out before a response was read.</summary>
    public int TransportErrors { get; private set; }

    /// <summary>Total bytes of request bodies this worker sent.</summary>
    public long RequestBytes { get; private set; }

    /// <summary>Timestamp at which this worker sent its first measured request.</summary>
    public long FirstStart { get; private set; } = long.MaxValue;

    /// <summary>Timestamp at which this worker's last measured request completed.</summary>
    public long LastEnd { get; private set; } = long.MinValue;

    /// <summary>Raw <see cref="Stopwatch"/> timestamp deltas of the measured requests.</summary>
    public IReadOnlyList<long> LatencyTimestamps => _latencyTimestamps;

    /// <summary>Records a measured request outcome.</summary>
    /// <param name="start">Raw <see cref="Stopwatch"/> timestamp taken before the request was sent.</param>
    /// <param name="end">Raw <see cref="Stopwatch"/> timestamp taken once the response was read.</param>
    /// <param name="requestBytes">Size of the request body sent.</param>
    /// <param name="outcome">How the request ended.</param>
    public void Add(long start, long end, int requestBytes, RequestOutcome outcome)
    {
        _latencyTimestamps.Add(end - start);
        RequestBytes += requestBytes;

        if (start < FirstStart)
        {
            FirstStart = start;
        }

        if (end > LastEnd)
        {
            LastEnd = end;
        }

        switch (outcome)
        {
            case RequestOutcome.Success:
                Succeeded++;
                break;
            case RequestOutcome.RpcError:
                RpcErrors++;
                break;
            case RequestOutcome.HttpError:
                HttpErrors++;
                break;
            default:
                TransportErrors++;
                break;
        }
    }
}
