// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.BeaconChain.StateTransition;
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
/// mainnet.
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

    /// <summary>The status returned by the most recent <see cref="NewPayload"/> call.</summary>
    public PayloadStatusV1? LastNewPayloadStatus { get; private set; }

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
        ExecutionPayloadV3 payload;
        try
        {
            payload = PayloadConverter.ToExecutionPayloadV3(body.ExecutionPayload!);
        }
        catch (OverflowException e)
        {
            if (_logger.IsWarn) _logger.Warn($"Execution payload at slot {message.Slot} has out-of-range fields: {e.Message}");
            return LastNewPayloadStatus = PayloadStatusV1.Invalid(null, e.Message);
        }

        Metrics.BeaconChainNewPayloadCalls++;
        // EIP-4788: the payload's parent_beacon_block_root is the parent root of the beacon block carrying it.
        ResultWrapper<PayloadStatusV1> result = await detector.InnerEngine.engine_newPayloadV4(
            payload,
            PayloadConverter.ToBlobVersionedHashes(body.BlobKzgCommitments),
            message.ParentRoot,
            PayloadConverter.ToExecutionRequestsList(body.ExecutionRequests));
        return LastNewPayloadStatus = Unwrap(result.Result, result.Data, "newPayloadV4");
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
    /// SYNCING/ACCEPTED count as (optimistic) acceptance per the spec's
    /// <c>verify_and_notify_new_payload</c>; only INVALID rejects the block.
    /// </remarks>
    public bool NotifyNewPayload(BeaconBlockBody body)
    {
        SignedBeaconBlock block = CurrentBlock ?? throw new InvalidOperationException($"{nameof(CurrentBlock)} must be set before running the state transition");
        if (!ReferenceEquals(block.Message?.Body, body))
            throw new InvalidOperationException($"The body being processed does not belong to {nameof(CurrentBlock)}");

        PayloadStatusV1 status = NewPayload(block).GetAwaiter().GetResult();
        return status.Status is not PayloadStatus.Invalid;
    }

    /// <remarks>
    /// An engine error is not a verdict on the block — treat it like an unavailable execution
    /// client and report SYNCING so the caller proceeds optimistically.
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
