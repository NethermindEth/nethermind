// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using Multiformats.Address;
using Multiformats.Address.Protocols;
using Nethermind.BeaconChain.Api;
using Nethermind.Core.Collections;
using Nethermind.Core.Caching;
using Nethermind.Core.Metric;
using Nethermind.Libp2p.Core;

namespace Nethermind.BeaconChain.P2P.ReqResp.Protocols;

/// <summary>Why an eth2 req/resp exchange failed, for <see cref="Metrics.BeaconChainReqRespFailures"/>.</summary>
public enum ReqRespFailureReason
{
    /// <summary>The stream's timeout fired: TTFB, per-chunk, or the overall streamed-response ceiling.</summary>
    Timeout,

    /// <summary>A peer sent a request or response chunk that failed framing or protocol validation.</summary>
    InvalidMessage,

    /// <summary>A protocol-level cap was hit: too many chunks, or too many concurrent inbound streams.</summary>
    LimitExceeded,

    /// <summary>A peer answered with a non-success response code.</summary>
    PeerError,

    /// <summary>The channel failed below the req/resp framing: a read, write or half-close did not succeed.</summary>
    Transport,
}

/// <summary>An outbound request cut by one of its bounds; the message names the bound and what the request was waiting for.</summary>
internal sealed class ReqRespTimeoutException(string message, Exception? cause = null) : TimeoutException(message, cause)
{
    /// <summary>The session never opened the request's channel, so the request never reached the peer.</summary>
    public bool ChannelNeverOpened { get; init; }
    /// <summary>No request of ours to any peer was answered while this one waited, so the silence is not held against the peer.</summary>
    public bool NotBlamed { get; set; }
}

/// <summary>Label key for <see cref="Metrics.BeaconChainReqRespFailures"/>: protocol id plus failure reason.</summary>
public readonly record struct ReqRespFailureKey(string ProtocolId, ReqRespFailureReason Reason) : IMetricLabels
{
    public string[] Labels => [ProtocolId, Reason.ToString()];
}

/// <summary>Shared timeouts and stream plumbing for the eth2 req/resp protocols.</summary>
public abstract class ReqRespProtocolBase
{
    /// <summary>The spec <c>TTFB_TIMEOUT</c>: maximum time to wait for the first response byte.</summary>
    internal static readonly TimeSpan DefaultTtfbTimeout = TimeSpan.FromSeconds(5);

    internal TimeSpan TtfbTimeout { get; init; } = DefaultTtfbTimeout;

    /// <summary>The spec <c>RESP_TIMEOUT</c>: maximum time for each subsequent response chunk.</summary>
    internal static readonly TimeSpan DefaultRespTimeout = TimeSpan.FromSeconds(10);

    internal TimeSpan RespTimeout { get; init; } = DefaultRespTimeout;

    /// <summary>The spec <c>MAX_CONCURRENT_REQUESTS</c>: max concurrent inbound streams per peer for one protocol id.</summary>
    /// <remarks>Consensus-specs v1.7.0-beta.2 req/resp requesting side: it MUST NOT make more concurrent requests than this with the same protocol ID.</remarks>
    protected internal const int MaxConcurrentRequests = 2;

    // Per protocol-id (one dictionary per singleton protocol instance), counts inbound streams
    // currently being served for a given remote peer, to enforce MaxConcurrentRequests.
    private readonly ConcurrentDictionary<PeerId, int> _inboundRequestsByPeer = new();

    // Shared budget for sessions whose remote peer id is unresolved, so identity cannot be withheld
    // to escape the per-peer cap.
    private int _unattributedInboundRequests;

    /// <summary>Maximum completed requests awaiting requester closure per peer, or across unidentified sessions.</summary>
    protected const int MaxLingeringRequests = 3 * MaxConcurrentRequests;

    private readonly ConcurrentDictionary<PeerId, int> _lingeringByPeer = new();
    private int _unattributedLingering;

    private readonly Lock _quotaLock = new();
    private readonly LruCache<PeerId, RequestQuota> _quotas = new(4096, "beacon reqresp peer quotas");
    private readonly LruCache<UInt128, RequestQuota> _hostQuotas = new(4096, "beacon reqresp host quotas");
    private RequestQuota? _globalQuota;
    private RequestQuota? _unattributedQuota;
    internal TimeProvider QuotaTime { get; init; } = TimeProvider.System;
    private sealed class RequestQuota(double tokens, DateTimeOffset updated)
    {
        public double Tokens = tokens;
        public DateTimeOffset Updated = updated;

        public void Refill(DateTimeOffset now, ulong capacity)
        {
            Tokens = Math.Min(capacity, Tokens + Math.Max(0, (now - Updated).TotalSeconds) * capacity / 10);
            Updated = now;
        }
    }

    /// <summary>Peer ceiling that sizes the global bucket, so a full honest peer set cannot drain it.</summary>
    internal int MaxPeerCount { get; init; } = 80;

    /// <summary>Whether the peer manager admitted a peer; when set, only admitted peers are charged against the global budget, and others are refused.</summary>
    internal Func<PeerId, bool>? IsAdmitted { get; init; }

    /// <summary>Peers' worth of budget one remote host may spend, so several honest nodes behind one address are still served.</summary>
    internal const int PeersPerHost = 4;

    /// <summary>Charges decoded work against the per-peer ten-second token bucket and, unless <paramref name="perPeerOnly"/>, a global one and one per remote host.</summary>
    /// <remarks>
    /// A host is an IPv4 address or an IPv6 /64, so reconnecting from one host with fresh keys draws on the budget the host already spent.
    /// The host bucket holds <see cref="PeersPerHost"/> peer buckets to serve NAT and multi-node hosts, which stays a small share of the global bucket of <see cref="MaxPeerCount"/> peer buckets.
    /// </remarks>
    /// <exception cref="Eth2ReqRespException">Code 139: a budget is spent, or a global charge comes from a peer that is not admitted.</exception>
    protected void ChargeRequest(ISessionContext context, ulong items, ulong capacity, bool perPeerOnly = false)
    {
        // Any key can open a session, so fresh identities could drain the global budget before admission refuses them.
        if (!perPeerOnly && IsAdmitted is { } isAdmitted && (context.State.RemotePeerId is not { } requester || !isAdmitted(requester)))
            throw new Eth2ReqRespException("Request from a peer that is not admitted", ReqRespFraming.ResponseCode.RateLimited);
        items = Math.Max(1, items);
        lock (_quotaLock)
        {
            DateTimeOffset now = QuotaTime.GetUtcNow();
            ulong globalCapacity = capacity * (ulong)Math.Max(1, MaxPeerCount);
            ulong hostCapacity = capacity * PeersPerHost;
            RequestQuota? global = null;
            RequestQuota? host = null;
            if (!perPeerOnly)
            {
                global = _globalQuota ??= new(globalCapacity, now);
                global.Refill(now, globalCapacity);
                if (HostKey(context.State.RemoteAddress) is { } hostKey)
                {
                    if (!_hostQuotas.TryGet(hostKey, out host!))
                    {
                        host = new(hostCapacity, now);
                        _hostQuotas.Set(hostKey, host);
                    }

                    host.Refill(now, hostCapacity);
                }
            }

            PeerId? peer = context.State.RemotePeerId;
            RequestQuota quota;
            if (peer is null)
            {
                quota = _unattributedQuota ??= new(capacity, now);
            }
            else if (!_quotas.TryGet(peer, out quota!))
            {
                quota = new(capacity, now);
                _quotas.Set(peer, quota);
            }

            quota.Refill(now, capacity);
            if (quota.Tokens < items || global is not null && global.Tokens < items || host is not null && host.Tokens < items)
                throw new Eth2ReqRespException("Request rate limit exceeded", ReqRespFraming.ResponseCode.RateLimited);
            quota.Tokens -= items;
            if (global is not null) global.Tokens -= items;
            if (host is not null) host.Tokens -= items;
        }
    }

    private static UInt128? HostKey(Multiaddress? address)
    {
        if (address is null) return null;
        foreach (MultiaddressProtocol protocol in address.Protocols)
        {
            if (protocol is IP4 or IP6 && protocol.Value is IPAddress ip) return StateRequestLimiter.ClientKey(ip);
        }

        return null;
    }

    /// <summary>Creates a timeout source; re-arm with <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/> per chunk.</summary>
    /// <remarks>
    /// Deliberately not linked to the channel's own cancellation: channel teardown must surface as
    /// end of stream (see <see cref="ChannelStreamAdapter"/>), not as a cancelled operation.
    /// </remarks>
    protected static CancellationTokenSource StartTimeout(TimeSpan timeout) => new(timeout);

    /// <summary>A per-chunk timeout (re-armed like <see cref="StartTimeout"/>'s result) additionally bounded by an overall ceiling that is never re-armed.</summary>
    protected readonly struct BoundedTimeout(CancellationTokenSource cts, CancellationTokenSource overall, TimeSpan initial, TimeSpan ceiling, TimeSpan responseTimeout) : IDisposable
    {
        public CancellationTokenSource Cts { get; } = cts;

        /// <summary>The failure of a read that this timeout cut, naming the bound that fired.</summary>
        /// <param name="chunksRead">The chunks read before the cut: none means the first-chunk bound, otherwise the bound between chunks.</param>
        /// <param name="cause">The cancellation the cut raised.</param>
        public TimeoutException Expired(int chunksRead, Exception cause) => new ReqRespTimeoutException(
            overall.IsCancellationRequested ? $"timed out after {Seconds(ceiling)}, the bound for the whole response, with {chunksRead} chunks read"
            : chunksRead == 0 ? $"timed out after {Seconds(initial)} waiting for the first chunk"
            : $"timed out after {Seconds(responseTimeout)} reading chunk {chunksRead + 1}",
            cause);

        public void Dispose()
        {
            Cts.Dispose();
            overall.Dispose();
        }
    }

    /// <summary>Starts a re-armable chunk timeout with an overall ceiling that is never re-armed.</summary>
    /// <remarks>The overall ceiling is implementation-defined: chunk bounds alone let drip-fed responses keep exchanges open indefinitely.</remarks>
    protected BoundedTimeout StartBoundedTimeout(TimeSpan initial, TimeSpan overallCeiling)
    {
        CancellationTokenSource overall = new(overallCeiling);
        CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
        cts.CancelAfter(initial);
        return new BoundedTimeout(cts, overall, initial, overallCeiling, RespTimeout);
    }

    internal static string Seconds(TimeSpan duration) => $"{duration.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s";

    /// <summary>Writes the request and half-closes the channel within <paramref name="bound"/>.</summary>
    /// <exception cref="ReqRespTimeoutException">The bound fired, named in the message.</exception>
    protected static async Task WriteRequestAndEofAsync(IChannel channel, Stream output, ReadOnlyMemory<byte> ssz, TimeSpan bound)
    {
        using CancellationTokenSource cts = StartTimeout(bound);
        try
        {
            await WriteRequestAndEofAsync(channel, output, ssz, cts.Token);
        }
        catch (Exception e) when (e is not Eth2ReqRespException && cts.IsCancellationRequested)
        {
            throw new ReqRespTimeoutException($"timed out after {Seconds(bound)} writing the request", e);
        }
    }

    protected static async Task WriteRequestAndEofAsync(IChannel channel, Stream stream, ReadOnlyMemory<byte> ssz, CancellationToken token)
    {
        await ReqRespFraming.WriteRequestAsync(stream, ssz, token);
        await WriteEofAsync(channel, token);
    }

    /// <summary>Half-closes the write side, signalling the end of the request per the req/resp spec.</summary>
    protected static async Task WriteEofAsync(IChannel channel, CancellationToken token)
    {
        if (await channel.WriteEofAsync(token) != IOResult.Ok)
        {
            throw new IOException("Failed to half-close the request stream");
        }
    }

    protected static Eth2ReqRespException ErrorChunkToException(ResponseChunk chunk) =>
        new($"Peer responded with error code {chunk.Result}: {System.Text.Encoding.UTF8.GetString(chunk.Payload)}", chunk.Result);

    protected static void RecordFailure(string protocolId, ReqRespFailureReason reason) =>
        Metrics.BeaconChainReqRespFailures.Increment(new ReqRespFailureKey(protocolId, reason));

    /// <summary>Records an inbound request this node refused, by the response code it answers with.</summary>
    /// <remarks>Only an invalid request is the requester's fault; a rate limit is a cap this node enforces, and any other code is this node's own failure.</remarks>
    protected static void RecordRefusal(string protocolId, byte responseCode)
    {
        if (responseCode == ReqRespFraming.ResponseCode.InvalidRequest) RecordFailure(protocolId, ReqRespFailureReason.InvalidMessage);
        else if (responseCode == ReqRespFraming.ResponseCode.RateLimited) RecordFailure(protocolId, ReqRespFailureReason.LimitExceeded);
    }

    /// <summary>Reserves a protocol slot under MaxConcurrentRequests; dispose it after serving.</summary>
    /// <returns>Null, recording LimitExceeded, when capped; the listener responds with a rate-limit error.</returns>
    /// <remarks>Unidentified sessions share one budget so withholding identity cannot evade the cap.</remarks>
    protected InboundRequest? TryEnterInbound(ISessionContext context, string protocolId)
    {
        PeerId? peerId = context.State.RemotePeerId;
        if (peerId is null)
        {
            if (Interlocked.Increment(ref _unattributedInboundRequests) <= MaxConcurrentRequests)
            {
                return new InboundRequest(this, context, protocolId, new UnattributedSlot(this));
            }

            Interlocked.Decrement(ref _unattributedInboundRequests);
            RecordFailure(protocolId, ReqRespFailureReason.LimitExceeded);
            return null;
        }

        int count = _inboundRequestsByPeer.AddOrUpdate(peerId, 1, static (_, existing) => existing + 1);
        if (count <= MaxConcurrentRequests)
        {
            return new InboundRequest(this, context, protocolId, new InboundSlot(_inboundRequestsByPeer, peerId));
        }

        Release(_inboundRequestsByPeer, peerId);
        RecordFailure(protocolId, ReqRespFailureReason.LimitExceeded);
        return null;
    }

    // Bound listeners awaiting requester closure after the response ends (consensus-specs networking, Req/Resp interaction).
    private LingerSlot? TryEnterLinger(PeerId? peerId)
    {
        if (peerId is null)
        {
            if (Interlocked.Increment(ref _unattributedLingering) <= MaxLingeringRequests)
            {
                return new LingerSlot(this, null);
            }

            Interlocked.Decrement(ref _unattributedLingering);
            return null;
        }

        if (_lingeringByPeer.AddOrUpdate(peerId, 1, static (_, existing) => existing + 1) <= MaxLingeringRequests)
        {
            return new LingerSlot(this, peerId);
        }

        Release(_lingeringByPeer, peerId);
        return null;
    }

    /// <remarks>Remove zero-count entries to bound the table under peer churn.</remarks>
    private static void Release(ConcurrentDictionary<PeerId, int> counts, PeerId peerId)
    {
        while (counts.TryGetValue(peerId, out int current))
        {
            int next = current - 1;
            if (next <= 0)
            {
                if (((ICollection<KeyValuePair<PeerId, int>>)counts).Remove(new KeyValuePair<PeerId, int>(peerId, current)))
                {
                    return;
                }
            }
            else if (counts.TryUpdate(peerId, next, current))
            {
                return;
            }
        }
    }

    /// <summary>Receives the peer of an inbound request that broke the protocol after the listener began serving it, for the peer manager to score.</summary>
    internal Action<PeerId, string>? RequestViolationSink { get; init; }
    /// <summary>How long a served stream is held for the requester to end it, so a requester that never does cannot pin a listener for good.</summary>
    internal TimeSpan WatchLingerAfterServed { get; init; } = DefaultRespTimeout;

    private void ReportViolation(ISessionContext context, string protocolId, string detail)
    {
        RecordFailure(protocolId, ReqRespFailureReason.InvalidMessage);
        if (context.State.RemotePeerId is { } peerId)
        {
            RequestViolationSink?.Invoke(peerId, $"{protocolId}: {detail}");
        }
    }

    /// <summary>Holds an inbound concurrency slot and observes trailing request bytes while serving.</summary>
    /// <remarks>
    /// Serving starts after payload read, without waiting for requester EOF. Disposal sends response EOF, releases the
    /// concurrency slot, then waits up to WatchLingerAfterServed for requester closure. MaxLingeringRequests caps these waits;
    /// excess listeners return immediately (consensus-specs networking Req/Resp interaction).
    /// </remarks>
    protected sealed class InboundRequest(ReqRespProtocolBase owner, ISessionContext context, string protocolId, IDisposable slot) : IAsyncDisposable
    {
        private readonly CancellationTokenSource _watchEnd = new();
        private Stream? _stream;
        private Task? _watching;

        /// <exception cref="Eth2ReqRespException">The request is malformed, or bytes after it are already buffered.</exception>
        public async Task<byte[]> ReadRequestAsync(Stream stream, int maxSize, CancellationToken token, bool allowEmpty = false)
        {
            try
            {
                (byte[] payload, ReqRespFraming.RequestTail tail) = await ReqRespFraming.ReadRequestWithTailAsync(stream, maxSize, token, allowEmpty);
                Watch(stream, tail);
                return payload;
            }
            catch (Eth2ReqRespException)
            {
                WatchRejected(stream);
                throw;
            }
        }

        /// <summary>Accepts a request that has no content, such as <c>metadata</c>.</summary>
        /// <exception cref="Eth2ReqRespException">Bytes are already buffered.</exception>
        public async Task AcceptRequestWithoutPayloadAsync(Stream stream, CancellationToken token)
        {
            try
            {
                await ReqRespFraming.RejectTrailingBytesAsync(stream, token);
                Watch(stream, ReqRespFraming.RequestTail.Open);
            }
            catch (Eth2ReqRespException)
            {
                WatchRejected(stream);
                throw;
            }
        }

        private void WatchRejected(Stream stream)
        {
            if (stream is ChannelStreamAdapter channel)
            {
                _stream = stream;
                _watching = ObserveClosureAsync(channel);
            }
        }

        private async Task ObserveClosureAsync(ChannelStreamAdapter channel)
        {
            try
            {
                await channel.WaitForCloseAsync(_watchEnd.Token);
            }
            catch (OperationCanceledException)
            {
                // Listener teardown closes the transport after the bounded linger expires.
            }
        }

        private void Watch(Stream stream, ReqRespFraming.RequestTail tail)
        {
            if (!tail.IsClosed)
            {
                _stream = stream;
                _watching = ObserveAsync(stream, tail);
            }
        }

        // Runs beside serving, so whatever goes wrong here must stay out of the response.
        private async Task ObserveAsync(Stream stream, ReqRespFraming.RequestTail tail)
        {
            try
            {
                string? violation = await tail.WatchAsync(stream, _watchEnd.Token);
                if (violation is not null)
                {
                    owner.ReportViolation(context, protocolId, violation);
                }
            }
            catch (Exception)
            {
                // Awaited only when disposing, so an escaping exception must not fail the listener.
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_watching is not null)
                {
                    using LingerSlot? linger = owner.TryEnterLinger(context.State.RemotePeerId);
                    _watchEnd.CancelAfter(owner.WatchLingerAfterServed);
                    if (_stream is ChannelStreamAdapter channel)
                    {
                        await channel.TryWriteEofAsync(_watchEnd.Token);
                    }

                    slot.Dispose();
                    if (linger is null)
                    {
                        _watchEnd.Cancel();
                    }

                    await _watching;
                }
            }
            finally
            {
                _watchEnd.Dispose();
                slot.Dispose();
            }
        }
    }

    private sealed class LingerSlot(ReqRespProtocolBase owner, PeerId? peerId) : IDisposable
    {
        public void Dispose()
        {
            if (peerId is null)
            {
                Interlocked.Decrement(ref owner._unattributedLingering);
            }
            else
            {
                Release(owner._lingeringByPeer, peerId);
            }
        }
    }

    private sealed class UnattributedSlot(ReqRespProtocolBase owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.Decrement(ref owner._unattributedInboundRequests);
            }
        }
    }

    private sealed class InboundSlot(ConcurrentDictionary<PeerId, int> counts, PeerId peerId) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            Release(counts, peerId);
        }
    }
}

/// <summary>An eth2 req/resp protocol whose response is a single success chunk without context bytes.</summary>
public abstract class SingleChunkProtocol<TRequest, TResponse> : ReqRespProtocolBase, ISessionProtocol<TRequest, TResponse>
{
    public abstract string Id { get; }
    /// <summary>Maximum framed request size, or zero for a request without a payload.</summary>
    protected abstract int MaxRequestSize { get; }
    protected abstract int MaxResponseSize { get; }
    protected abstract byte[] EncodeRequest(TRequest request);
    /// <exception cref="Eth2ReqRespException">Malformed SSZ must use this exception type so ListenAsync normalizes failed exchanges.</exception>
    protected abstract TRequest DecodeRequest(byte[] ssz);
    protected abstract byte[] EncodeResponse(TResponse response);
    /// <exception cref="Eth2ReqRespException">Malformed SSZ must use this exception type so DialAsync normalizes failed exchanges.</exception>
    protected abstract TResponse DecodeResponse(byte[] ssz);
    /// <summary>Produces the listen-side response for a decoded request.</summary>
    protected abstract TResponse HandleRequest(TRequest request);

    private TResponse DecodeWithMetrics(byte[] payload)
    {
        try
        {
            return DecodeResponse(payload);
        }
        catch (Exception e) when (e is not Eth2ReqRespException and not OperationCanceledException)
        {
            RecordFailure(Id, ReqRespFailureReason.InvalidMessage);
            throw;
        }
    }

    public virtual async Task<TResponse> DialAsync(IChannel downChannel, ISessionContext context, TRequest request)
    {
        using RequestTiming.Exchange exchange = RequestTiming.Open(request);
        RequestTiming? timing = exchange.Timing;
        using ChannelStreamAdapter input = new(downChannel);
        using CancellationTokenSource cts = StartTimeout(TtfbTimeout + RespTimeout);
        bool classified = false;
        try
        {
            ResponseChunk chunk;
            try
            {
                if (MaxRequestSize == 0)
                {
                    await WriteEofAsync(downChannel, cts.Token);
                }
                else
                {
                    await WriteRequestAndEofAsync(downChannel, input, EncodeRequest(request), cts.Token);
                }
                ResponseChunk? read = await ReqRespFraming.ReadResponseChunkAsync(input, 0, MaxResponseSize, cts.Token);
                // A clean close before any byte is the peer or this node ending the session, not a malformed response.
                classified = read is null;
                chunk = read ?? throw new Eth2ReqRespException("Peer closed without responding");
            }
            catch (Exception e) when (e is not Eth2ReqRespException && cts.IsCancellationRequested)
            {
                RecordFailure(Id, ReqRespFailureReason.Timeout);
                throw new ReqRespTimeoutException($"timed out after {Seconds(TtfbTimeout + RespTimeout)} waiting for the response", e);
            }
            catch (IOException)
            {
                RecordFailure(Id, ReqRespFailureReason.Transport);
                throw;
            }

            timing?.ChunkRead();
            if (chunk.Result == ReqRespFraming.ResponseCode.Success)
            {
                return DecodeWithMetrics(chunk.Payload);
            }

            classified = true;
            RecordFailure(Id, ReqRespFailureReason.PeerError);
            throw ErrorChunkToException(chunk);
        }
        catch (OperationCanceledException)
        {
            RecordFailure(Id, ReqRespFailureReason.Timeout);
            throw;
        }
        catch (Eth2ReqRespException) when (!classified)
        {
            RecordFailure(Id, ReqRespFailureReason.InvalidMessage);
            throw;
        }
    }

    /// <summary>Receives each request about to be answered with success, and the peer that sent it, before the answer is written.</summary>
    /// <remarks>Runs first so that whatever it records is in place when the requester, once answered, sends its next request.</remarks>
    internal Action<PeerId, TRequest>? RequestServed { get; init; }

    public async Task ListenAsync(IChannel downChannel, ISessionContext context)
    {
        Stream stream = new ChannelStreamAdapter(downChannel);
        await using InboundRequest? inboundSlot = TryEnterInbound(context, Id);
        if (inboundSlot is null)
        {
            using CancellationTokenSource refusal = StartTimeout(TtfbTimeout);
            await ReqRespFraming.WriteErrorChunkAsync(stream, ReqRespFraming.ResponseCode.RateLimited, "Too many concurrent requests", refusal.Token);
            return;
        }

        using CancellationTokenSource cts = StartTimeout(RespTimeout);
        try
        {
            TRequest request;
            if (MaxRequestSize == 0)
            {
                await inboundSlot.AcceptRequestWithoutPayloadAsync(stream, cts.Token);
                request = default!;
            }
            else
            {
                request = DecodeRequest(await inboundSlot.ReadRequestAsync(stream, MaxRequestSize, cts.Token));
            }
            ChargeRequest(context, 1, 32, perPeerOnly: true);
            byte[] response = EncodeResponse(HandleRequest(request));
            if (context.State.RemotePeerId is { } requester) RequestServed?.Invoke(requester, request);
            await ReqRespFraming.WriteResponseChunkAsync(stream, ReqRespFraming.ResponseCode.Success, default, response, cts.Token);
        }
        catch (Eth2ReqRespException e)
        {
            RecordRefusal(Id, e.ResponseCode);
            await ReqRespFraming.WriteErrorChunkAsync(stream, e.ResponseCode, e.Message, cts.Token);
        }
        catch (OperationCanceledException)
        {
            RecordFailure(Id, ReqRespFailureReason.Timeout);
        }
    }
}
