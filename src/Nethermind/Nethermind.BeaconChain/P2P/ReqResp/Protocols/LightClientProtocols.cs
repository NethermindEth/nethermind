// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Libp2p.Core;

namespace Nethermind.BeaconChain.P2P.ReqResp.Protocols;

/// <summary>Bounded outbound light-client req/resp with fork-context validation.</summary>
public abstract class LightClientProtocol<TRequest, TResponse>(BeaconChainSpec spec) : ReqRespProtocolBase, ISessionProtocol<TRequest, TResponse>
{
    private const int MaxResponseSize = 256 * 1024;
    protected BeaconChainSpec Spec => spec;

    public abstract string Id { get; }
    protected abstract int MaxRequestSize { get; }
    protected abstract byte[] EncodeRequest(TRequest request);
    protected abstract TResponse DecodeResponse(byte[] ssz);
    protected abstract ulong ResponseSlot(TResponse response);
    protected virtual bool HasRequestBody => true;

    public async Task<TResponse> DialAsync(IChannel downChannel, ISessionContext context, TRequest request)
    {
        using ChannelStreamAdapter stream = new(downChannel);
        using CancellationTokenSource timeout = StartTimeout(TtfbTimeout + RespTimeout);
        if (HasRequestBody)
        {
            await WriteRequestAndEofAsync(downChannel, stream, EncodeRequest(request), timeout.Token);
        }
        else
        {
            await WriteEofAsync(downChannel, timeout.Token);
        }

        ResponseChunk chunk = await ReqRespFraming.ReadResponseChunkAsync(stream, ReqRespFraming.ForkContextLength, MaxResponseSize, timeout.Token)
            ?? throw new Eth2ReqRespException("Peer closed without a light-client response");
        if (chunk.Result != ReqRespFraming.ResponseCode.Success) throw ErrorChunkToException(chunk);

        TResponse response;
        try
        {
            response = DecodeResponse(chunk.Payload);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new Eth2ReqRespException($"Invalid light-client SSZ response: {e.Message}");
        }

        ulong slot = ResponseSlot(response);
        byte[] expectedDigest = ForkDigest.Compute(spec, spec.GetEpoch(slot));
        if (!chunk.ContextBytes.AsSpan().SequenceEqual(expectedDigest))
        {
            throw new Eth2ReqRespException("Light-client response fork digest does not match its slot");
        }

        if (await ReqRespFraming.ReadResponseChunkAsync(stream, ReqRespFraming.ForkContextLength, MaxResponseSize, timeout.Token) is not null)
        {
            throw new Eth2ReqRespException("Expected one light-client response chunk");
        }

        return response;
    }

    public async Task ListenAsync(IChannel downChannel, ISessionContext context)
    {
        using ChannelStreamAdapter stream = new(downChannel);
        await using InboundRequest? inbound = TryEnterInbound(context, Id);
        if (inbound is null) return;
        using CancellationTokenSource timeout = StartTimeout(RespTimeout);
        try
        {
            if (HasRequestBody)
            {
                await inbound.ReadRequestAsync(stream, MaxRequestSize, timeout.Token);
            }
            else
            {
                await inbound.AcceptRequestWithoutPayloadAsync(stream, timeout.Token);
            }

            await ReqRespFraming.WriteErrorChunkAsync(stream, ReqRespFraming.ResponseCode.ResourceUnavailable,
                "Light-client data unavailable", timeout.Token);
        }
        catch (Eth2ReqRespException e)
        {
            await ReqRespFraming.WriteErrorChunkAsync(stream, e.ResponseCode, e.Message, timeout.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }
}

/// <summary>Requests a light-client bootstrap for one trusted block root.</summary>
public sealed class LightClientBootstrapProtocol(BeaconChainSpec spec) : LightClientProtocol<Hash256, LightClientBootstrap>(spec)
{
    public override string Id => "/eth2/beacon_chain/req/light_client_bootstrap/1/ssz_snappy";
    protected override int MaxRequestSize => Hash256.Size;
    protected override byte[] EncodeRequest(Hash256 request) => request.Bytes.ToArray();
    protected override LightClientBootstrap DecodeResponse(byte[] ssz)
        => LightClientWireCodec.DecodeBootstrap(ssz, Spec);
    protected override ulong ResponseSlot(LightClientBootstrap response) => response.Header?.Beacon?.Slot
        ?? throw new Eth2ReqRespException("Bootstrap has no beacon header");
}

/// <summary>Requests one light-client update at the given sync-committee period.</summary>
public sealed class LightClientUpdatesByRangeProtocol(BeaconChainSpec spec) : LightClientProtocol<ulong, LightClientUpdate>(spec)
{
    public override string Id => "/eth2/beacon_chain/req/light_client_updates_by_range/1/ssz_snappy";
    protected override int MaxRequestSize => 16;
    protected override byte[] EncodeRequest(ulong period)
    {
        byte[] ssz = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(ssz, period);
        BinaryPrimitives.WriteUInt64LittleEndian(ssz.AsSpan(8), 1);
        return ssz;
    }
    protected override LightClientUpdate DecodeResponse(byte[] ssz)
        => LightClientWireCodec.DecodeUpdate(ssz, Spec);
    protected override ulong ResponseSlot(LightClientUpdate response) => response.AttestedHeader?.Beacon?.Slot
        ?? throw new Eth2ReqRespException("Update has no attested header");
}

/// <summary>Requests the latest light-client finality update.</summary>
public sealed class LightClientFinalityUpdateProtocol(BeaconChainSpec spec) : LightClientProtocol<ulong, LightClientFinalityUpdate>(spec)
{
    public override string Id => "/eth2/beacon_chain/req/light_client_finality_update/1/ssz_snappy";
    protected override int MaxRequestSize => 0;
    protected override bool HasRequestBody => false;
    protected override byte[] EncodeRequest(ulong request) => [];
    protected override LightClientFinalityUpdate DecodeResponse(byte[] ssz)
        => LightClientWireCodec.DecodeFinality(ssz, Spec);
    protected override ulong ResponseSlot(LightClientFinalityUpdate response) => response.AttestedHeader?.Beacon?.Slot
        ?? throw new Eth2ReqRespException("Finality update has no attested header");
}

/// <summary>Requests the latest light-client optimistic update.</summary>
public sealed class LightClientOptimisticUpdateProtocol(BeaconChainSpec spec) : LightClientProtocol<ulong, LightClientOptimisticUpdate>(spec)
{
    public override string Id => "/eth2/beacon_chain/req/light_client_optimistic_update/1/ssz_snappy";
    protected override int MaxRequestSize => 0;
    protected override bool HasRequestBody => false;
    protected override byte[] EncodeRequest(ulong request) => [];
    protected override LightClientOptimisticUpdate DecodeResponse(byte[] ssz) => LightClientWireCodec.DecodeOptimistic(ssz, Spec);
    protected override ulong ResponseSlot(LightClientOptimisticUpdate response) => response.AttestedHeader?.Beacon?.Slot
        ?? throw new Eth2ReqRespException("Optimistic update has no attested header");
}
