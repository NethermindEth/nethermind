// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Libp2p.Core;

namespace Nethermind.BeaconChain.P2P.ReqResp.Protocols;

/// <summary>Shared timeouts and stream plumbing for the eth2 req/resp protocols.</summary>
public abstract class ReqRespProtocolBase
{
    /// <summary>The spec <c>TTFB_TIMEOUT</c>: maximum time to wait for the first response byte.</summary>
    protected static readonly TimeSpan TtfbTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The spec <c>RESP_TIMEOUT</c>: maximum time for each subsequent response chunk.</summary>
    protected static readonly TimeSpan RespTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Creates a timeout source; re-arm with <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/> per chunk.</summary>
    /// <remarks>
    /// Deliberately not linked to the channel's own cancellation: channel teardown must surface as
    /// end of stream (see <see cref="ChannelStreamAdapter"/>), not as a cancelled operation.
    /// </remarks>
    protected static CancellationTokenSource StartTimeout(TimeSpan timeout) => new(timeout);

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
        return chunk.Result == ReqRespFraming.ResponseCode.Success ? DecodeResponse(chunk.Payload) : throw ErrorChunkToException(chunk);
    }

    public async Task ListenAsync(IChannel downChannel, ISessionContext context)
    {
        Stream stream = new ChannelStreamAdapter(downChannel);
        using CancellationTokenSource cts = StartTimeout(RespTimeout);
        try
        {
            TRequest request = DecodeRequest(await ReqRespFraming.ReadRequestAsync(stream, MaxRequestSize, cts.Token));
            await ReqRespFraming.WriteResponseChunkAsync(stream, ReqRespFraming.ResponseCode.Success, default, EncodeResponse(HandleRequest(request)), cts.Token);
        }
        catch (Eth2ReqRespException e)
        {
            await ReqRespFraming.WriteErrorChunkAsync(stream, e.ResponseCode, e.Message, cts.Token);
        }
    }
}
