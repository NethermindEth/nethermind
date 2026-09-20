// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;

namespace Nethermind.BeaconChain.P2P.ReqResp.Protocols;

/// <summary>Chunked execution payload envelope response plumbing shared by the Gloas <c>execution_payload_envelopes_by_range</c> and <c>by_root</c> protocols.</summary>
public abstract class ExecutionPayloadEnvelopesProtocolBase(BeaconChainSpec spec) : ReqRespProtocolBase
{
    /// <summary>The spec <c>MAX_REQUEST_PAYLOADS</c>.</summary>
    public const ulong MaxRequestPayloads = 128;

    /// <summary>Not a spec constant (see <see cref="BlocksProtocolBase.MaxBlocksResponseDuration"/>): bounds the total wall-clock time a streamed envelopes response may take.</summary>
    protected static readonly TimeSpan MaxEnvelopesResponseDuration = TimeSpan.FromSeconds(60);

    protected BeaconChainSpec Spec { get; } = spec;

    /// <summary>
    /// The context bytes of an envelope chunk: the fork digest of the envelope's slot epoch. The
    /// envelope container itself carries no slot field, so this reads <c>payload.slot_number</c>
    /// (EIP-7843's <c>SLOTNUM</c>: "the beacon chain slot number of the current block" — the same
    /// value as the envelope's beacon block's slot).
    /// </summary>
    /// <remarks>Callers must ensure <paramref name="envelope"/> has a non-null <c>Message.Payload</c> first (see <see cref="ReadEnvelopeChunksAsync"/>); this indexes into it unconditionally.</remarks>
    protected byte[] ContextBytesFor(SignedExecutionPayloadEnvelope envelope) =>
        ForkDigest.Compute(Spec, Spec.GetEpoch(envelope.Message!.Payload!.SlotNumber));

    /// <param name="overallTimeout">Overrides <see cref="MaxEnvelopesResponseDuration"/>; test-only seam, production call sites omit it.</param>
    protected async Task<IReadOnlyList<SignedExecutionPayloadEnvelope>> ReadEnvelopeChunksAsync(Stream stream, int maxEnvelopes, string protocolId, TimeSpan? overallTimeout = null)
    {
        List<SignedExecutionPayloadEnvelope> envelopes = [];
        using BoundedTimeout timeout = StartBoundedTimeout(TtfbTimeout + RespTimeout, overallTimeout ?? MaxEnvelopesResponseDuration);
        CancellationTokenSource cts = timeout.Cts;
        try
        {
            while (await ReqRespFraming.ReadResponseChunkAsync(stream, ReqRespFraming.ForkContextLength, ReqRespFraming.MaxPayloadSize, cts.Token) is { } chunk)
            {
                if (chunk.Result != ReqRespFraming.ResponseCode.Success)
                {
                    RecordFailure(protocolId, ReqRespFailureReason.PeerError);
                    throw ErrorChunkToException(chunk);
                }

                if (envelopes.Count >= maxEnvelopes)
                {
                    RecordFailure(protocolId, ReqRespFailureReason.LimitExceeded);
                    throw new Eth2ReqRespException($"Peer responded with more than the requested {maxEnvelopes} execution payload envelopes");
                }

                SignedExecutionPayloadEnvelope envelope;
                try
                {
                    SignedExecutionPayloadEnvelope.Decode(chunk.Payload, out envelope);
                }
                catch (Exception e) when (e is not Eth2ReqRespException and not OperationCanceledException)
                {
                    RecordFailure(protocolId, ReqRespFailureReason.InvalidMessage);
                    throw new Eth2ReqRespException($"Malformed execution payload envelope chunk: {e.Message}");
                }

                if (envelope.Message?.Payload is null || envelope.Message.BeaconBlockRoot is null)
                {
                    RecordFailure(protocolId, ReqRespFailureReason.InvalidMessage);
                    throw new Eth2ReqRespException("Execution payload envelope chunk missing its payload or beacon block root");
                }

                if (!chunk.ContextBytes.AsSpan().SequenceEqual(ContextBytesFor(envelope)))
                {
                    RecordFailure(protocolId, ReqRespFailureReason.InvalidMessage);
                    throw new Eth2ReqRespException($"Execution payload envelope chunk context bytes do not match the fork digest of slot {envelope.Message.Payload.SlotNumber}");
                }

                envelopes.Add(envelope);
                cts.CancelAfter(RespTimeout);
            }
        }
        catch (OperationCanceledException)
        {
            RecordFailure(protocolId, ReqRespFailureReason.Timeout);
            throw;
        }

        return envelopes;
    }

    protected Task WriteEnvelopeChunkAsync(Stream stream, SignedExecutionPayloadEnvelope envelope, CancellationTokenSource cts)
    {
        cts.CancelAfter(RespTimeout);
        return ReqRespFraming.WriteResponseChunkAsync(stream, ReqRespFraming.ResponseCode.Success, ContextBytesFor(envelope), SignedExecutionPayloadEnvelope.Encode(envelope), cts.Token);
    }
}
