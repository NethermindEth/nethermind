// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Collections;
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
    protected static readonly TimeSpan TtfbTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The spec <c>RESP_TIMEOUT</c>: maximum time for each subsequent response chunk.</summary>
    protected static readonly TimeSpan RespTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The spec <c>MAX_CONCURRENT_REQUESTS</c>: max concurrent inbound streams per peer for one protocol id.</summary>
    protected const int MaxConcurrentRequests = 2;

    // Per protocol-id (one dictionary per singleton protocol instance), counts inbound streams
    // currently being served for a given remote peer, to enforce MaxConcurrentRequests.
    private readonly ConcurrentDictionary<PeerId, int> _inboundRequestsByPeer = new();

    // Shared budget for sessions whose remote peer id is unresolved, so identity cannot be withheld
    // to escape the per-peer cap.
    private int _unattributedInboundRequests;

    /// <summary>Creates a timeout source; re-arm with <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/> per chunk.</summary>
    /// <remarks>
    /// Deliberately not linked to the channel's own cancellation: channel teardown must surface as
    /// end of stream (see <see cref="ChannelStreamAdapter"/>), not as a cancelled operation.
    /// </remarks>
    protected static CancellationTokenSource StartTimeout(TimeSpan timeout) => new(timeout);

    /// <summary>A per-chunk timeout (re-armed like <see cref="StartTimeout"/>'s result) additionally bounded by an overall ceiling that is never re-armed.</summary>
    protected readonly struct BoundedTimeout(CancellationTokenSource cts, CancellationTokenSource overall) : IDisposable
    {
        public CancellationTokenSource Cts { get; } = cts;

        public void Dispose()
        {
            Cts.Dispose();
            overall.Dispose();
        }
    }

    /// <summary>
    /// Starts a re-armable per-chunk timeout whose token also fires once <paramref name="overallCeiling"/>
    /// elapses, regardless of how often the per-chunk deadline is re-armed. Without this, a peer that
    /// drip-feeds one valid chunk just under the per-chunk timeout can hold a streamed request open
    /// indefinitely: the spec's TTFB_TIMEOUT/RESP_TIMEOUT bound a single chunk, not the whole exchange,
    /// and the current spec deprecated them without introducing a replacement overall deadline (see
    /// consensus-specs PR #3767) — so <paramref name="overallCeiling"/> is this implementation's own
    /// bound, not a spec constant.
    /// </summary>
    protected static BoundedTimeout StartBoundedTimeout(TimeSpan initial, TimeSpan overallCeiling)
    {
        CancellationTokenSource overall = new(overallCeiling);
        CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
        cts.CancelAfter(initial);
        return new BoundedTimeout(cts, overall);
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

    /// <summary>
    /// Reserves an inbound slot for <paramref name="protocolId"/> from the peer identified by
    /// <paramref name="context"/>, enforcing <see cref="MaxConcurrentRequests"/>. Returns <c>null</c>
    /// (after recording a <see cref="ReqRespFailureReason.LimitExceeded"/> failure) when the peer
    /// already holds the cap for this protocol id; the caller must then refuse the stream without
    /// reading or writing to it. Dispose the returned slot once the request has been served.
    /// </summary>
    /// <remarks>
    /// A session whose remote peer id is not resolved is metered against one shared budget rather
    /// than let through: an unattributable stream is exactly the one an attacker would arrange, so
    /// the cap must not be escapable by withholding identity.
    /// </remarks>
    protected IDisposable? TryEnterInbound(ISessionContext context, string protocolId)
    {
        PeerId? peerId = context.State.RemotePeerId;
        if (peerId is null)
        {
            if (Interlocked.Increment(ref _unattributedInboundRequests) <= MaxConcurrentRequests)
            {
                return new UnattributedSlot(this);
            }

            Interlocked.Decrement(ref _unattributedInboundRequests);
            RecordFailure(protocolId, ReqRespFailureReason.LimitExceeded);
            return null;
        }

        int count = _inboundRequestsByPeer.AddOrUpdate(peerId, 1, static (_, existing) => existing + 1);
        if (count <= MaxConcurrentRequests)
        {
            return new InboundSlot(_inboundRequestsByPeer, peerId);
        }

        Release(_inboundRequestsByPeer, peerId);
        RecordFailure(protocolId, ReqRespFailureReason.LimitExceeded);
        return null;
    }

    /// <summary>Decrements a peer's in-flight count, removing the entry at zero.</summary>
    /// <remarks>Leaving zero-count entries behind would grow this dictionary for as long as the
    /// process runs, which peer churn alone would then turn into an unbounded leak.</remarks>
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

    protected abstract int MaxRequestSize { get; }
    protected abstract int MaxResponseSize { get; }
    protected abstract byte[] EncodeRequest(TRequest request);
    protected abstract TRequest DecodeRequest(byte[] ssz);
    protected abstract byte[] EncodeResponse(TResponse response);
    protected abstract TResponse DecodeResponse(byte[] ssz);

    /// <summary>Produces the listen-side response for a decoded request.</summary>
    protected abstract TResponse HandleRequest(TRequest request);

    public async Task<TResponse> DialAsync(IChannel downChannel, ISessionContext context, TRequest request)
    {
        Stream stream = new ChannelStreamAdapter(downChannel);
        using CancellationTokenSource cts = StartTimeout(TtfbTimeout + RespTimeout);
        await WriteRequestAndEofAsync(downChannel, stream, EncodeRequest(request), cts.Token);
        ResponseChunk chunk = await ReqRespFraming.ReadResponseChunkAsync(stream, 0, MaxResponseSize, cts.Token)
            ?? throw new Eth2ReqRespException("Peer closed the stream without responding");
        if (chunk.Result == ReqRespFraming.ResponseCode.Success)
        {
            return DecodeResponse(chunk.Payload);
        }

        RecordFailure(Id, ReqRespFailureReason.PeerError);
        throw ErrorChunkToException(chunk);
    }

    public async Task ListenAsync(IChannel downChannel, ISessionContext context)
    {
        Stream stream = new ChannelStreamAdapter(downChannel);
        using IDisposable? inboundSlot = TryEnterInbound(context, Id);
        if (inboundSlot is null)
        {
            return;
        }

        using CancellationTokenSource cts = StartTimeout(RespTimeout);
        try
        {
            TRequest request = DecodeRequest(await ReqRespFraming.ReadRequestAsync(stream, MaxRequestSize, cts.Token));
            await ReqRespFraming.WriteResponseChunkAsync(stream, ReqRespFraming.ResponseCode.Success, default, EncodeResponse(HandleRequest(request)), cts.Token);
        }
        catch (Eth2ReqRespException e)
        {
            RecordFailure(Id, ReqRespFailureReason.InvalidMessage);
            await ReqRespFraming.WriteErrorChunkAsync(stream, e.ResponseCode, e.Message, cts.Token);
        }
        catch (OperationCanceledException)
        {
            RecordFailure(Id, ReqRespFailureReason.Timeout);
        }
    }
}
