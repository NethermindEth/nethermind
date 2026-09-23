// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Attributes;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.BeaconChain.Sync;

/// <summary>The outcome of running a Gloas execution payload envelope through <see cref="ExecutionPayloadEnvelopeImporter"/>.</summary>
public enum ExecutionPayloadEnvelopeImportResult
{
    /// <summary>The envelope verified and the execution layer reported its payload VALID.</summary>
    Valid,

    /// <summary>
    /// The envelope verified and the execution layer neither rejected nor validated its payload
    /// (SYNCING, ACCEPTED); it must not be treated as <see cref="Valid"/>.
    /// </summary>
    Optimistic,

    /// <summary>
    /// No post-state is known for <c>envelope.beacon_block_root</c>. The envelope is not invalid:
    /// it may have arrived before its block, and can be retried once that block imports.
    /// </summary>
    UnknownBlock,

    /// <summary>The blob data the block's committed bid names is not (yet) available; retry once it is held.</summary>
    DataUnavailable,

    /// <summary>The envelope failed a spec check or its payload was reported INVALID; it is a rejected message.</summary>
    Invalid,

    /// <summary>The execution layer could not be consulted; the envelope is unevaluated and must be retried.</summary>
    EngineUnavailable,
}

/// <summary>
/// Spec <c>on_execution_payload_envelope</c> (specs/gloas/fork-choice.md) up to, but not including,
/// its store write: resolves the named block's frozen post-state, checks data availability, and
/// runs <see cref="GloasBlockProcessing.VerifyExecutionPayloadEnvelope"/> including the engine call.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here mutates fork choice. The caller owns <c>store.payloads</c> and the execution status:
/// on <see cref="ExecutionPayloadEnvelopeImportResult.Valid"/> and
/// <see cref="ExecutionPayloadEnvelopeImportResult.Optimistic"/> it records the payload for
/// <c>envelope.beacon_block_root</c>; only on <see cref="ExecutionPayloadEnvelopeImportResult.Valid"/>
/// does it upgrade that block's execution status to valid.
/// </para>
/// <para>
/// Every call gets its own execution-verdict recorder, because an envelope is verified on a call
/// stack separate from its block's import; a verdict read from anywhere shared could belong to a
/// different payload. The recorder is a local of <see cref="Import"/>, never a field and never the
/// block import's, so no verdict outlives the call that produced it. Only
/// <see cref="ExecutionPayloadEnvelopeImportResult.Invalid"/> is counted as a fork-choice rejection;
/// the other non-verified outcomes are retriable. Not thread-safe; call from the import worker that
/// owns <c>states</c>.
/// </para>
/// </remarks>
/// <param name="states">The frozen Gloas post-states by block root (the spec store's <c>block_states</c>).</param>
/// <param name="engine">The execution layer the envelope's payload is submitted to.</param>
/// <param name="isDataAvailable">
/// Spec <c>is_data_available(beacon_block_root)</c>, given the block root and the bid that block
/// committed (whose <c>blob_kzg_commitments</c> the columns must match). Required so availability
/// can never be skipped by omission.
/// </param>
public sealed class ExecutionPayloadEnvelopeImporter(
    IGloasBlockStateProvider states,
    INewPayloadNotifier engine,
    PubkeyCache pubkeys,
    Func<Hash256, ExecutionPayloadBid, bool> isDataAvailable,
    ILogManager logManager)
{
    // No gossip_ prefix: envelopes also arrive by req/resp and from the local builder, and every source is judged here.
    private static readonly StringLabel EnvelopeRejected = new("execution_payload_envelope");

    private readonly ILogger _logger = logManager.GetClassLogger<ExecutionPayloadEnvelopeImporter>();

    /// <summary>Verifies <paramref name="signedEnvelope"/> against the post-state of the block it names and returns the verdict.</summary>
    public ExecutionPayloadEnvelopeImportResult Import(SignedExecutionPayloadEnvelope signedEnvelope)
    {
        Hash256? blockRoot = signedEnvelope.Message!.BeaconBlockRoot;
        if (blockRoot is null)
        {
            return Reject("it carries no beacon block root");
        }

        BeaconStateGloas? state = states.GetGloasBlockState(blockRoot);
        if (state is null)
        {
            if (_logger.IsTrace) _logger.Trace($"Envelope for unknown beacon block {blockRoot}");
            return ExecutionPayloadEnvelopeImportResult.UnknownBlock;
        }

        if (!isDataAvailable(blockRoot, state.LatestExecutionPayloadBid!))
        {
            if (_logger.IsTrace) _logger.Trace($"Envelope for beacon block {blockRoot} is waiting for its blob data");
            return ExecutionPayloadEnvelopeImportResult.DataUnavailable;
        }

        EnvelopeVerdict verdict = new(engine);
        try
        {
            GloasBlockProcessing.VerifyExecutionPayloadEnvelope(states, signedEnvelope, verdict, pubkeys);
        }
        catch (EngineUnavailableException e)
        {
            if (_logger.IsWarn) _logger.Warn($"Deferring envelope for beacon block {blockRoot}: {e.Message}");
            return ExecutionPayloadEnvelopeImportResult.EngineUnavailable;
        }
        catch (BeaconStateException e)
        {
            return Reject($"envelope for beacon block {blockRoot}: {e.Message}");
        }

        // VerifyExecutionPayloadEnvelope throws for INVALID and for no verdict, so only these two reach here.
        return verdict.Status switch
        {
            ExecutionStatus.Valid => ExecutionPayloadEnvelopeImportResult.Valid,
            ExecutionStatus.Optimistic => ExecutionPayloadEnvelopeImportResult.Optimistic,
            _ => throw new InvalidOperationException($"Envelope for beacon block {blockRoot} verified with execution verdict {verdict.Status?.ToString() ?? "none"}"),
        };
    }

    private ExecutionPayloadEnvelopeImportResult Reject(string reason)
    {
        Metrics.BeaconChainForkChoiceRejections.Increment(EnvelopeRejected);
        if (_logger.IsDebug) _logger.Debug($"Rejected execution payload envelope: {reason}");
        return ExecutionPayloadEnvelopeImportResult.Invalid;
    }

    /// <summary>Carries one envelope's execution verdict from the verification's engine call back to <see cref="Import"/>.</summary>
    private sealed class EnvelopeVerdict(INewPayloadNotifier engine) : INewPayloadNotifier
    {
        /// <summary>The engine's verdict on this envelope's payload; <c>null</c> until the engine answers.</summary>
        public ExecutionStatus? Status { get; private set; }

        public ExecutionStatus NotifyNewPayload(BeaconBlockBody body) =>
            throw new InvalidOperationException("An execution payload envelope is never notified through a block body");

        public ExecutionStatus NotifyNewPayload(ExecutionPayloadGloas payload, Hash256?[] versionedHashes, Hash256 parentBeaconBlockRoot, ExecutionRequestsGloas executionRequests)
        {
            ExecutionStatus status = engine.NotifyNewPayload(payload, versionedHashes, parentBeaconBlockRoot, executionRequests);
            Status = status;
            return status;
        }
    }
}
