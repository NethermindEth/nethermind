// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;
using Nethermind.Libp2p.Core;

namespace Nethermind.BeaconChain.P2P.ReqResp.Protocols;

/// <summary>The Gloas <c>execution_payload_envelopes_by_range</c> v1 protocol.</summary>
/// <remarks>
/// The listen side serves the envelopes of the current head's chain the local <see cref="ExecutionPayloadEnvelopePool"/>
/// holds (see <see cref="ExecutionPayloadEnvelopePool.GetCanonical"/>), skipping slots it does not hold. The dial side
/// validates per-chunk fork-digest context bytes (in the shared base) and strictly increasing slots within the requested range.
/// </remarks>
public sealed class ExecutionPayloadEnvelopesByRangeProtocol(BeaconChainSpec spec, ExecutionPayloadEnvelopePool pool) : ExecutionPayloadEnvelopesProtocolBase(spec),
    ISessionProtocol<ExecutionPayloadEnvelopesByRangeRequest, IReadOnlyList<SignedExecutionPayloadEnvelope>>
{
    private const int RequestLength = 2 * sizeof(ulong);

    public string Id => "/eth2/beacon_chain/req/execution_payload_envelopes_by_range/1/ssz_snappy";

    public async Task<IReadOnlyList<SignedExecutionPayloadEnvelope>> DialAsync(IChannel downChannel, ISessionContext context, ExecutionPayloadEnvelopesByRangeRequest request)
    {
        using RequestTiming.Exchange exchange = RequestTiming.Open(request);
        RequestTiming? timing = exchange.Timing;
        Stream stream = new ChannelStreamAdapter(downChannel);
        await WriteRequestAndEofAsync(downChannel, stream, ExecutionPayloadEnvelopesByRangeRequest.Encode(request), RespTimeout);

        int maxEnvelopes = (int)Math.Min(request.Count, MaxRequestPayloads);
        IReadOnlyList<SignedExecutionPayloadEnvelope> envelopes = await ReadEnvelopeChunksAsync(stream, maxEnvelopes, Id, timing: timing);

        ulong? previousSlot = null;
        foreach (SignedExecutionPayloadEnvelope envelope in envelopes)
        {
            ulong slot = envelope.Message!.Payload!.SlotNumber;
            if (slot < request.StartSlot || slot >= request.StartSlot + request.Count || slot <= previousSlot)
            {
                RecordFailure(Id, ReqRespFailureReason.InvalidMessage);
                throw new Eth2ReqRespException($"Execution payload envelope slot {slot} outside the requested range or out of order");
            }

            previousSlot = slot;
        }

        return envelopes;
    }

    public async Task ListenAsync(IChannel downChannel, ISessionContext context)
    {
        Stream stream = new ChannelStreamAdapter(downChannel);
        await using InboundRequest? inboundSlot = TryEnterInbound(context, Id);
        if (inboundSlot is null)
        {
            return;
        }

        using BoundedTimeout timeout = StartBoundedTimeout(RespTimeout, MaxEnvelopesResponseDuration);
        CancellationTokenSource cts = timeout.Cts;
        try
        {
            byte[] requestSsz = await inboundSlot.ReadRequestAsync(stream, RequestLength, cts.Token);
            if (requestSsz.Length != RequestLength)
            {
                throw new Eth2ReqRespException($"Execution-payload-envelopes-by-range request must be {RequestLength} bytes, got {requestSsz.Length}");
            }

            ExecutionPayloadEnvelopesByRangeRequest.Decode(requestSsz, out ExecutionPayloadEnvelopesByRangeRequest request);
            if (request.Count == 0)
            {
                throw new Eth2ReqRespException("Execution-payload-envelopes-by-range request count must be positive");
            }

            foreach (SignedExecutionPayloadEnvelope envelope in pool.GetCanonical(request.StartSlot, Math.Min(request.Count, MaxRequestPayloads)))
            {
                await WriteEnvelopeChunkAsync(stream, envelope, cts);
            }
        }
        catch (Eth2ReqRespException e)
        {
            // A walk past the cap or our own unreadable block is not the peer's fault.
            if (e.ResponseCode == ReqRespFraming.ResponseCode.InvalidRequest)
            {
                RecordFailure(Id, ReqRespFailureReason.InvalidMessage);
            }

            await ReqRespFraming.WriteErrorChunkAsync(stream, e.ResponseCode, e.Message, cts.Token);
        }
        catch (OperationCanceledException)
        {
            RecordFailure(Id, ReqRespFailureReason.Timeout);
        }
    }
}
