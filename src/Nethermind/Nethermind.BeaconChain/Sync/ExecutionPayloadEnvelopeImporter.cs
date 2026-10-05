// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.StateTransition.Hashing;
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

    /// <summary>The payload of <c>envelope.beacon_block_root</c> is already verified; the envelope was not evaluated again.</summary>
    AlreadyKnown,
}

/// <summary>
/// Spec <c>on_execution_payload_envelope</c> (specs/gloas/fork-choice.md) up to, but not including,
/// its store write: resolves the named block's frozen post-state, checks data availability, and
/// runs <see cref="GloasBlockProcessing.VerifyExecutionPayloadEnvelope"/> including the engine call.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here mutates fork choice. The caller owns <c>store.payloads</c>: on
/// <see cref="ExecutionPayloadEnvelopeImportResult.Valid"/> and
/// <see cref="ExecutionPayloadEnvelopeImportResult.Optimistic"/> it records the payload for
/// <c>envelope.beacon_block_root</c>; on VALID it also marks that block and its ancestors VALID and records the block's own
/// payload as VALID apart from its node status (see <see cref="IBlockImporter.ImportEnvelope"/>).
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
/// <param name="hasher">Supplies the hasher for the block-root check's state root, asked once per envelope; <c>null</c> merkleizes the whole state.</param>
public sealed class ExecutionPayloadEnvelopeImporter(
    IGloasBlockStateProvider states,
    INewPayloadNotifier engine,
    PubkeyCache pubkeys,
    Func<Hash256, ExecutionPayloadBid, bool> isDataAvailable,
    ILogManager logManager,
    Func<IBeaconStateHasher>? hasher = null)
{
    // No gossip_ prefix: envelopes also arrive by req/resp and from the local builder, and every source is judged here.
    private static readonly StringLabel EnvelopeRejected = new("execution_payload_envelope");

    private readonly ILogger _logger = logManager.GetClassLogger<ExecutionPayloadEnvelopeImporter>();

    /// <summary>Verifies <paramref name="signedEnvelope"/> against the post-state of the block it names and returns the verdict.</summary>
    /// <exception cref="InvalidOperationException">The engine notifier returned no verdict (<see cref="ExecutionStatus.Irrelevant"/>), which is a local fault and not a rejection.</exception>
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

        if (!HasSelfBuildProposerKey(signedEnvelope.Message, state))
        {
            return Reject($"envelope for beacon block {blockRoot}: self-build proposer {state.LatestBlockHeader!.ProposerIndex} is not in the validator registry");
        }

        EnvelopeVerdict verdict = new(engine);
        try
        {
            GloasBlockProcessing.VerifyExecutionPayloadEnvelope(states, signedEnvelope, verdict, pubkeys, hasher?.Invoke());
        }
        catch (EngineUnavailableException e)
        {
            if (_logger.IsWarn) _logger.Warn($"Deferring envelope for beacon block {blockRoot}: {e.Message}");
            return ExecutionPayloadEnvelopeImportResult.EngineUnavailable;
        }
        catch (BeaconStateException e) when (verdict.Status is ExecutionStatus.Irrelevant)
        {
            // The notifier is called last, so this is its no-verdict refusal: our bug, never the sender's.
            throw new InvalidOperationException($"Envelope for beacon block {blockRoot} got no execution verdict from the notifier", e);
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

    /// <summary>Checks <paramref name="signedEnvelope"/>'s signature against the post-state of the block it names.</summary>
    /// <returns><c>null</c> when that state is not held; otherwise whether the signer the state names for it signed it.</returns>
    public bool? VerifySignature(SignedExecutionPayloadEnvelope signedEnvelope)
    {
        if (signedEnvelope.Message?.BeaconBlockRoot is not { } blockRoot || states.GetGloasBlockState(blockRoot) is not { } state)
        {
            return null;
        }

        return HasSelfBuildProposerKey(signedEnvelope.Message, state) && GloasBlockProcessing.VerifyExecutionPayloadEnvelopeSignature(state, signedEnvelope, pubkeys);
    }

    /// <summary>Extends the validator cache before verifying a self-built envelope. Registered builders do not need it; an out-of-registry proposer is invalid.</summary>
    private bool HasSelfBuildProposerKey(ExecutionPayloadEnvelope envelope, BeaconStateGloas state)
    {
        if (envelope.BuilderIndex != Presets.BuilderIndexSelfBuild)
        {
            return true;
        }

        ulong proposerIndex = state.LatestBlockHeader!.ProposerIndex;
        if (proposerIndex < (ulong)pubkeys.Count)
        {
            return true;
        }

        Validator[] validators = state.Validators!;
        if (proposerIndex >= (ulong)validators.Length)
        {
            return false;
        }

        pubkeys.Extend(validators, pubkeys.Count);
        return true;
    }

    private ExecutionPayloadEnvelopeImportResult Reject(string reason)
    {
        Metrics.BeaconChainForkChoiceRejections.Increment(EnvelopeRejected);
        if (_logger.IsDebug) _logger.Debug($"Rejected execution payload envelope: {reason}");
        return ExecutionPayloadEnvelopeImportResult.Invalid;
    }

    private sealed class EnvelopeVerdict(INewPayloadNotifier engine) : INewPayloadNotifier
    {
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
