// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.BeaconChain.Engine;

/// <summary>
/// Drives the execution layer through in-process engine API calls — <c>engine_newPayloadV4</c> and
/// <c>engine_forkchoiceUpdatedV3</c>, the methods an external consensus client uses on Fulu-era
/// mainnet, and <c>engine_newPayloadV5</c> for the execution payload envelopes Gloas delivers
/// separately from the block.
/// </summary>
/// <remarks>
/// Calls go through <see cref="ExternalClDetector.InnerEngine"/> so the driver's own traffic never
/// trips external-CL detection. The orchestrator serializes all calls (they run on the slot
/// worker); only the last-status properties are meant to be read concurrently.
/// </remarks>
public sealed class EngineDriver(ExternalClDetector detector, ILogManager logManager) : IEngineDriver
{
    private readonly ILogger _logger = logManager.GetClassLogger<EngineDriver>();

    /// <summary>
    /// The block currently being run through the state transition; the orchestrator sets it before
    /// <see cref="StateTransition.StateTransition.Apply"/> so <see cref="NotifyNewPayload"/> can
    /// recover what the body alone does not carry — the EIP-4788 parent beacon block root.
    /// </summary>
    public SignedBeaconBlock? CurrentBlock { get; set; }

    /// <summary>
    /// Whether the execution layer has ever answered a <see cref="NewPayload(SignedBeaconBlock)"/>
    /// call, including one that failed in process.
    /// </summary>
    /// <remarks>
    /// A failed call counts as answered: <see cref="Unwrap"/> cannot tell an unreachable execution
    /// layer from one that is genuinely syncing, so this reports only that the path has been driven.
    /// </remarks>
    public bool HasAnsweredNewPayload { get; private set; }

    /// <summary>The status returned by the most recent <see cref="ForkchoiceUpdated"/> call.</summary>
    public PayloadStatusV1? LastForkchoiceStatus { get; private set; }

    /// <summary>
    /// Submits the block's execution payload via <c>engine_newPayloadV4</c> and returns the
    /// execution layer's verdict (VALID/INVALID/SYNCING/ACCEPTED).
    /// </summary>
    public async Task<PayloadStatusV1> NewPayload(SignedBeaconBlock block)
    {
        BeaconBlock message = block.Message!;
        BeaconBlockBody body = message.Body!;
        ExecutionPayloadV3 payload = PayloadConverter.ToExecutionPayloadV3(body.ExecutionPayload!);

        Metrics.BeaconChainNewPayloadCalls++;
        // EIP-4788: the payload's parent_beacon_block_root is the parent root of the beacon block carrying it.
        ResultWrapper<PayloadStatusV1> result = await detector.InnerEngine.engine_newPayloadV4(
            payload,
            PayloadConverter.ToBlobVersionedHashes(body.BlobKzgCommitments),
            message.ParentRoot,
            PayloadConverter.ToExecutionRequestsList(body.ExecutionRequests));
        return UnwrapNewPayload(result.Result, result.Data, "newPayloadV4");
    }

    /// <summary>
    /// Applies the fork-choice state via <c>engine_forkchoiceUpdatedV3</c> (no payload attributes)
    /// and returns the head status, including SYNCING while the execution layer catches up.
    /// </summary>
    public async Task<PayloadStatusV1> ForkchoiceUpdated(Hash256 headExecHash, Hash256 safeExecHash, Hash256 finalizedExecHash)
    {
        Metrics.BeaconChainForkchoiceUpdatedCalls++;
        ResultWrapper<ForkchoiceUpdatedV1Result> result = await detector.InnerEngine.engine_forkchoiceUpdatedV3(
            new ForkchoiceStateV1(headExecHash, finalizedExecHash, safeExecHash));
        return LastForkchoiceStatus = Unwrap(result.Result, result.Data?.PayloadStatus, "forkchoiceUpdatedV3");
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Bridges the synchronous transition hook onto <see cref="NewPayload"/> for
    /// <see cref="CurrentBlock"/>. Blocking on the call is acceptable here: the state transition
    /// runs on a dedicated worker thread and the in-process engine call never re-enters it.
    /// SYNCING/ACCEPTED map to <see cref="ExecutionStatus.Optimistic"/> per the spec's
    /// <c>verify_and_notify_new_payload</c>; only INVALID rejects the block.
    /// </remarks>
    public ExecutionStatus NotifyNewPayload(BeaconBlockBody body)
    {
        SignedBeaconBlock block = CurrentBlock ?? throw new InvalidOperationException($"{nameof(CurrentBlock)} must be set before running the state transition");
        if (!ReferenceEquals(block.Message?.Body, body))
            throw new InvalidOperationException($"The body being processed does not belong to {nameof(CurrentBlock)}");

        PayloadStatusV1 status = NewPayload(block).GetAwaiter().GetResult();
        return ToExecutionStatus(status.Status);
    }

    /// <summary>
    /// Submits a Gloas execution payload envelope's payload via <c>engine_newPayloadV5</c> and
    /// returns the execution layer's verdict. Unlike <see cref="NewPayload(SignedBeaconBlock)"/> it
    /// needs no <see cref="CurrentBlock"/>: the envelope carries its own parent beacon block root.
    /// </summary>
    public async Task<PayloadStatusV1> NewPayload(ExecutionPayloadGloas payload, Hash256?[] versionedHashes, Hash256 parentBeaconBlockRoot, ExecutionRequestsGloas executionRequests)
    {
        Metrics.BeaconChainNewPayloadCalls++;
        ResultWrapper<PayloadStatusV1> result = await detector.InnerEngine.engine_newPayloadV5(
            PayloadConverter.ToExecutionPayloadV4(payload),
            versionedHashes,
            parentBeaconBlockRoot,
            PayloadConverter.ToExecutionRequestsList(executionRequests));
        return UnwrapNewPayload(result.Result, result.Data, "newPayloadV5");
    }

    /// <inheritdoc/>
    /// <remarks>Same mapping as the block-body overload: only INVALID rejects the envelope.</remarks>
    public ExecutionStatus NotifyNewPayload(ExecutionPayloadGloas payload, Hash256?[] versionedHashes, Hash256 parentBeaconBlockRoot, ExecutionRequestsGloas executionRequests)
    {
        PayloadStatusV1 status = NewPayload(payload, versionedHashes, parentBeaconBlockRoot, executionRequests).GetAwaiter().GetResult();
        return ToExecutionStatus(status.Status);
    }

    /// <summary>Maps an engine API payload status onto the fork choice execution status.</summary>
    /// <remarks>
    /// Anything that is neither VALID nor INVALID is optimistic acceptance: the payload was not
    /// rejected and was not validated either. That covers SYNCING, ACCEPTED and
    /// INCLUSION_LIST_UNSATISFIED (EIP-7805). Collapsing them onto VALID would admit a block to
    /// fork choice as validated when it was not, which cannot be unwound once fork choice holds a
    /// <see cref="ExecutionStatus.Valid"/> node.
    /// </remarks>
    private static ExecutionStatus ToExecutionStatus(string? status) => status switch
    {
        PayloadStatus.Valid => ExecutionStatus.Valid,
        PayloadStatus.Invalid => ExecutionStatus.Invalid,
        _ => ExecutionStatus.Optimistic,
    };

    /// <summary>
    /// Unwraps a <c>newPayload</c> result and records that the execution layer answered.
    /// </summary>
    /// <remarks>
    /// Both <c>newPayload</c> overloads go through here so neither can record the answer and the
    /// other forget to; <see cref="ForkchoiceUpdated"/> deliberately does not, because a
    /// fork-choice call that fails is not a statement about any particular block.
    /// </remarks>
    /// <exception cref="EngineUnavailableException">The call produced no verdict.</exception>
    private PayloadStatusV1 UnwrapNewPayload(Result result, PayloadStatusV1? status, string method)
    {
        HasAnsweredNewPayload = true;
        if (result.ResultType == ResultType.Success && status is not null)
        {
            return status;
        }

        if (_logger.IsError) _logger.Error($"In-process engine_{method} call failed: {result.Error}");
        throw new EngineUnavailableException(method, result.Error);
    }

    /// <remarks>
    /// Only <see cref="ForkchoiceUpdated"/> reports SYNCING for a failed call, because it is a
    /// statement about the head rather than a verdict on a block the caller is about to import.
    /// The <c>newPayload</c> path goes through <see cref="UnwrapNewPayload"/> and throws instead.
    /// </remarks>
    private PayloadStatusV1 Unwrap(Result result, PayloadStatusV1? status, string method)
    {
        if (result.ResultType == ResultType.Success && status is not null)
        {
            return status;
        }

        if (_logger.IsError) _logger.Error($"In-process engine_{method} call failed: {result.Error}");
        return PayloadStatusV1.Syncing;
    }
}
