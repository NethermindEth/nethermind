// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Libp2p.Core;

namespace Nethermind.BeaconChain.P2P.ReqResp.Protocols;

/// <summary>The Gloas <c>execution_payload_envelopes_by_root</c> v1 protocol.</summary>
/// <remarks>
/// The listen side serves whichever requested roots the local <see cref="ExecutionPayloadEnvelopePool"/>
/// has. The dial side rejects an envelope whose beacon block root was not requested.
/// </remarks>
public sealed class ExecutionPayloadEnvelopesByRootProtocol(BeaconChainSpec spec, ExecutionPayloadEnvelopePool pool) : ExecutionPayloadEnvelopesProtocolBase(spec),
    ISessionProtocol<Hash256[], IReadOnlyList<SignedExecutionPayloadEnvelope>>
{
    private const int MaxRequestLength = (int)MaxRequestPayloads * Hash256.Size;

    public string Id => "/eth2/beacon_chain/req/execution_payload_envelopes_by_root/1/ssz_snappy";

    public async Task<IReadOnlyList<SignedExecutionPayloadEnvelope>> DialAsync(IChannel downChannel, ISessionContext context, Hash256[] request)
    {
        if (request.Length > (int)MaxRequestPayloads)
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.Length, $"Cannot request more than {MaxRequestPayloads} beacon block roots in a single request");
        }

        Stream stream = new ChannelStreamAdapter(downChannel);
        using (CancellationTokenSource cts = StartTimeout(RespTimeout))
        {
            await WriteRequestAndEofAsync(downChannel, stream, ExecutionPayloadEnvelopeRoots.Encode(new ExecutionPayloadEnvelopeRoots { Roots = request }), cts.Token);
        }

        IReadOnlyList<SignedExecutionPayloadEnvelope> envelopes = await ReadEnvelopeChunksAsync(stream, request.Length, Id);
        HashSet<Hash256> requestedRoots = [.. request];
        foreach (SignedExecutionPayloadEnvelope envelope in envelopes)
        {
            if (!requestedRoots.Remove(envelope.Message!.BeaconBlockRoot!))
            {
                RecordFailure(Id, ReqRespFailureReason.InvalidMessage);
                throw new Eth2ReqRespException("Peer responded with an execution payload envelope that was not requested");
            }
        }

        return envelopes;
    }

    public async Task ListenAsync(IChannel downChannel, ISessionContext context)
    {
        Stream stream = new ChannelStreamAdapter(downChannel);
        using IDisposable? inboundSlot = TryEnterInbound(context, Id);
        if (inboundSlot is null)
        {
            return;
        }

        using BoundedTimeout timeout = StartBoundedTimeout(RespTimeout, MaxEnvelopesResponseDuration);
        CancellationTokenSource cts = timeout.Cts;
        try
        {
            byte[] requestSsz = await ReqRespFraming.ReadRequestAsync(stream, MaxRequestLength, cts.Token);
            ExecutionPayloadEnvelopeRoots request;
            try
            {
                ExecutionPayloadEnvelopeRoots.Decode(requestSsz, out request);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                throw new Eth2ReqRespException($"Malformed execution-payload-envelopes-by-root request: {e.Message}");
            }

            foreach (Hash256 root in request.Roots ?? [])
            {
                if (pool.TryGet(root, out SignedExecutionPayloadEnvelope? envelope))
                {
                    await WriteEnvelopeChunkAsync(stream, envelope!, cts);
                }
            }
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
