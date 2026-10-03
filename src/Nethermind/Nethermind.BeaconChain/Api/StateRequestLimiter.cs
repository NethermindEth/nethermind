// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.BeaconChain.Api.Common;
using Nethermind.BeaconChain.Spec;

namespace Nethermind.BeaconChain.Api;

/// <summary>Bounds the Beacon API requests that read a full beacon state: how many run at once, how many one client runs at once, and how often one client may download one.</summary>
/// <remarks>
/// Each such request holds a whole persisted state in memory, so an unbounded number of them is a
/// memory exhaustion vector for a node that serves checkpoint sync. Saturation is refused at once,
/// never queued: 503 when the node is busy, 429 when one client already has such a request in flight
/// or exceeds its download rate, and a response is aborted when, once started, it goes unwritten for the idle
/// bound or runs past the total cap, so a stalled reader cannot hold every permit while a slow but
/// steady download of a large state still completes. The
/// beacon-APIs v5.0.0-alpha.2 state routes list neither status; 503 carries the spec's
/// <c>ErrorMessage</c> shape as its <c>CurrentlySyncing</c> response does.
/// </remarks>
internal sealed class StateRequestLimiter : IDisposable
{
    private static readonly PathString StatesPrefix = new("/eth/v1/beacon/states");
    private static readonly PathString DebugStatesPrefix = new("/eth/v2/debug/beacon/states");

    /// <summary>The longest deadline <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/> accepts, in whole seconds.</summary>
    internal const int MaxResponseTimeoutSeconds = 4294967;

    /// <summary>The largest single write forwarded to the response, so a slow reader shows progress within a chunk rather than only after a whole state.</summary>
    internal const int WriteChunkBytes = 64 * 1024;

    /// <summary>A lower bound on any beacon state's SSZ size: its fixed-length <c>randao_mixes</c>, <c>block_roots</c> and <c>state_roots</c> vectors (beacon-chain.md BeaconState).</summary>
    internal const long MinStateBytes = (long)(Presets.EpochsPerHistoricalVector + 2 * Presets.SlotsPerHistoricalRoot) * 32;

    private readonly int _maxConcurrent;
    private readonly int _maxPerClient;
    private readonly TimeSpan _responseTimeout;
    private readonly TimeSpan _idleTimeout;
    private readonly ConcurrencyLimiter _inFlight;
    private readonly PartitionedRateLimiter<HttpContext> _inFlightPerClient;
    private readonly PartitionedRateLimiter<HttpContext>? _downloadsPerClient;

    /// <exception cref="ArgumentOutOfRangeException">A limit in <paramref name="config"/> is out of range.</exception>
    public StateRequestLimiter(IBeaconApiConfig config)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(config.MaxConcurrentStateRequests, 1, nameof(IBeaconApiConfig.MaxConcurrentStateRequests));
        ArgumentOutOfRangeException.ThrowIfNegative(config.StateDownloadsPerMinutePerClient, nameof(IBeaconApiConfig.StateDownloadsPerMinutePerClient));

        ArgumentOutOfRangeException.ThrowIfLessThan(config.MaxConcurrentStateRequestsPerClient, 1, nameof(IBeaconApiConfig.MaxConcurrentStateRequestsPerClient));
        ArgumentOutOfRangeException.ThrowIfLessThan(config.StateResponseTimeoutSeconds, 1, nameof(IBeaconApiConfig.StateResponseTimeoutSeconds));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(config.StateResponseTimeoutSeconds, MaxResponseTimeoutSeconds, nameof(IBeaconApiConfig.StateResponseTimeoutSeconds));
        ArgumentOutOfRangeException.ThrowIfLessThan(config.StateResponseIdleTimeoutSeconds, 1, nameof(IBeaconApiConfig.StateResponseIdleTimeoutSeconds));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(config.StateResponseIdleTimeoutSeconds, MaxResponseTimeoutSeconds, nameof(IBeaconApiConfig.StateResponseIdleTimeoutSeconds));
        // A reader just fast enough to finish the smallest state within the total cap must be able to accept one chunk within the idle bound.
        long minIdleSeconds = (WriteChunkBytes * (long)config.StateResponseTimeoutSeconds + MinStateBytes - 1) / MinStateBytes;
        if (config.StateResponseIdleTimeoutSeconds < minIdleSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(IBeaconApiConfig.StateResponseIdleTimeoutSeconds), config.StateResponseIdleTimeoutSeconds,
                $"A reader just fast enough to download a {MinStateBytes}-byte state within {nameof(IBeaconApiConfig.StateResponseTimeoutSeconds)} ({config.StateResponseTimeoutSeconds} s) takes up to {minIdleSeconds} s to accept one {WriteChunkBytes}-byte chunk, so {nameof(IBeaconApiConfig.StateResponseIdleTimeoutSeconds)} must be at least {minIdleSeconds}; raise it or lower the total.");
        }

        _maxConcurrent = config.MaxConcurrentStateRequests;
        _responseTimeout = TimeSpan.FromSeconds(config.StateResponseTimeoutSeconds);
        _idleTimeout = TimeSpan.FromSeconds(config.StateResponseIdleTimeoutSeconds);
        int perClient = _maxPerClient = config.MaxConcurrentStateRequestsPerClient;
        _inFlight = new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = _maxConcurrent, QueueLimit = 0 });
        _inFlightPerClient = PartitionedRateLimiter.Create<HttpContext, UInt128>(c => RateLimitPartition.GetConcurrencyLimiter(
            ClientKey(c.Connection.RemoteIpAddress),
            _ => new ConcurrencyLimiterOptions { PermitLimit = perClient, QueueLimit = 0 }));

        int perMinute = config.StateDownloadsPerMinutePerClient;
        if (perMinute > 0)
        {
            _downloadsPerClient = PartitionedRateLimiter.Create<HttpContext, UInt128>(c => RateLimitPartition.GetTokenBucketLimiter(
                ClientKey(c.Connection.RemoteIpAddress),
                _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = perMinute,
                    TokensPerPeriod = perMinute,
                    ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }));
        }
    }

    public async Task InvokeAsync(HttpContext c, RequestDelegate next)
    {
        PathString path = c.Request.Path;
        bool download = path.StartsWithSegments(DebugStatesPrefix);
        if (!download && (!path.StartsWithSegments(StatesPrefix, out PathString stateRoute) || LoadsNoState(stateRoute)))
        {
            await next(c);
            return;
        }

        // Taken first, so a request refused as busy never spends the client's download budget.
        using RateLimitLease lease = _inFlight.AttemptAcquire();
        if (!lease.IsAcquired)
        {
            await ApiErrors.Write(c, StatusCodes.Status503ServiceUnavailable,
                $"The node is already serving {_maxConcurrent} beacon state requests; retry later.", c.RequestAborted);
            return;
        }

        using RateLimitLease ownLease = _inFlightPerClient.AttemptAcquire(c);
        if (!ownLease.IsAcquired)
        {
            await ApiErrors.Write(c, StatusCodes.Status429TooManyRequests,
                $"This client already has {_maxPerClient} beacon state {(_maxPerClient == 1 ? "request" : "requests")} in flight; retry once one completes.", c.RequestAborted);
            return;
        }

        if (download && _downloadsPerClient is not null)
        {
            using RateLimitLease clientLease = _downloadsPerClient.AttemptAcquire(c);
            if (!clientLease.IsAcquired)
            {
                if (clientLease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter))
                {
                    c.Response.Headers.RetryAfter = Math.Ceiling(retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                }

                await ApiErrors.Write(c, StatusCodes.Status429TooManyRequests, "Too many beacon state downloads from this client; retry later.", c.RequestAborted);
                return;
            }
        }

        CancellationToken clientAborted = c.RequestAborted;
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(clientAborted);
        deadline.CancelAfter(_responseTimeout);
        // Armed by the first write: loading a state before it is the node's own time, bounded by the total cap.
        using CancellationTokenSource idle = new(Timeout.InfiniteTimeSpan);
        using CancellationTokenRegistration idleExpiry = idle.Token.Register(static state => ((CancellationTokenSource)state!).Cancel(), deadline);
        // Endpoints observe RequestAborted, and Abort fails a write blocked on a reader that stopped reading.
        c.RequestAborted = deadline.Token;
        using CancellationTokenRegistration abort = deadline.Token.Register(static state => ((HttpContext)state!).Abort(), c);
        // Left in place once the request ends, so a late write from the server's own completion finds a disposed timer and ignores it.
        c.Response.Body = new ProgressStream(c.Response.Body, () =>
        {
            try
            {
                idle.CancelAfter(_idleTimeout);
            }
            catch (ObjectDisposedException)
            {
            }
        });
        try
        {
            await next(c);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !clientAborted.IsCancellationRequested)
        {
            // The idle bound or the total cap passed and the response was aborted; the permits are released as this method returns.
        }
    }

    /// <summary>Forwards writes to the response body in chunks of at most <see cref="WriteChunkBytes"/> and reports each chunk as it starts and completes, so one huge write cannot look like a stall and a first write that never completes still arms the idle bound.</summary>
    private sealed class ProgressStream(Stream inner, Action progress) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            while (!buffer.IsEmpty)
            {
                ReadOnlySpan<byte> chunk = buffer[..Math.Min(buffer.Length, WriteChunkBytes)];
                progress();
                inner.Write(chunk);
                progress();
                buffer = buffer[chunk.Length..];
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (!buffer.IsEmpty)
            {
                ReadOnlyMemory<byte> chunk = buffer[..Math.Min(buffer.Length, WriteChunkBytes)];
                progress();
                await inner.WriteAsync(chunk, cancellationToken);
                progress();
                buffer = buffer[chunk.Length..];
            }
        }

        public override void Flush()
        {
            inner.Flush();
            progress();
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await inner.FlushAsync(cancellationToken);
            progress();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>Whether a route under <c>/eth/v1/beacon/states</c> is <c>/{state_id}/root</c>, which reads the block's <c>state_root</c> rather than the state.</summary>
    private static bool LoadsNoState(PathString stateRoute)
    {
        ReadOnlySpan<char> route = stateRoute.Value.AsSpan().TrimEnd('/');
        int leaf = route.LastIndexOf('/');
        return leaf > 0 && route[..leaf].LastIndexOf('/') == 0 && route[(leaf + 1)..].Equals("root", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>One partition per IPv4 address and per IPv6 /64, so a client cannot mint fresh budgets by rotating the addresses of one IPv6 subnet.</summary>
    internal static UInt128 ClientKey(IPAddress? address)
    {
        if (address is null) return UInt128.MaxValue;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out int written);
        // An IPv6 key's low half is above every IPv4 key, so the two families never share a partition.
        return address.AddressFamily == AddressFamily.InterNetworkV6
            ? new UInt128(BinaryPrimitives.ReadUInt64BigEndian(bytes), 1UL << 32)
            : BinaryPrimitives.ReadUInt32BigEndian(bytes[..written]);
    }

    public void Dispose()
    {
        _inFlight.Dispose();
        _inFlightPerClient.Dispose();
        _downloadsPerClient?.Dispose();
    }
}
