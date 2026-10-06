// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Diagnostics;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.BeaconChain.Engine;

/// <summary>
/// Drives the execution layer through in-process engine API calls — <c>engine_newPayloadV4</c> and
/// <c>engine_forkchoiceUpdatedV3</c>, the methods an external consensus client uses on Fulu-era
/// mainnet, and <c>engine_newPayloadV5</c> for the execution payload envelopes Gloas delivers
/// separately from the block. From the Gloas fork on, <c>forkchoiceUpdated</c> goes through
/// <c>engine_forkchoiceUpdatedV4</c> so it can carry the node's custody columns.
/// </summary>
/// <remarks>
/// Calls go through <see cref="ExternalClDetector.InnerEngine"/> so the driver's own traffic never
/// trips external-CL detection. The orchestrator serializes all calls (they run on the slot
/// worker); only the last-status properties are meant to be read concurrently. A forkchoice
/// update still running past its deadline fails every later call until it finishes.
/// </remarks>
public sealed class EngineDriver(ExternalClDetector detector, ILogManager logManager, SlotClock clock, BeaconChainSpec spec, INodeColumnCustodySource custody) : IEngineDriver
{
    private readonly ILogger _logger = logManager.GetClassLogger<EngineDriver>();
    private volatile bool _isAvailable = true;
    private Task<PayloadStatusV1>? _pendingForkchoice;

    /// <summary>Whether the most recent engine call returned a verdict.</summary>
    public bool IsAvailable => _isAvailable;
    /// <summary>How long a caller waits for a forkchoice update before it counts as unavailable.</summary>
    internal TimeSpan ForkchoiceTimeout { get; init; } = TimeSpan.FromSeconds(8);
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
    /// A failed call counts as answered: this reports only that the path has been driven, not that
    /// the execution layer returned a verdict.
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

        Interlocked.Increment(ref Metrics.NewPayloadCallsCount);
        long started = Stopwatch.GetTimestamp();
        // EIP-4788: the payload's parent_beacon_block_root is the parent root of the beacon block carrying it.
        Hash256?[] versionedHashes = PayloadConverter.ToBlobVersionedHashes(body.BlobKzgCommitments);
        byte[][] requests = PayloadConverter.ToExecutionRequestsList(body.ExecutionRequests);
        return await CallEngineAsync("newPayloadV4", _logger.IsInfo ? $"{payload.BlockNumber} ({payload.BlockHash?.ToShortString()})" : null, async () =>
        {
            detector.ThrowIfStoodDown();
            ResultWrapper<PayloadStatusV1> result = await detector.InnerEngine.engine_newPayloadV4(payload, versionedHashes, message.ParentRoot, requests);
            Interlocked.Add(ref Metrics.NewPayloadMillisecondsCount, (ulong)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return UnwrapNewPayload(result.Result, result.Data, "newPayloadV4");
        }, body.Graffiti);
    }

    /// <summary>
    /// Applies the fork-choice state and returns the head status, including SYNCING while the
    /// execution layer catches up. Sends <c>engine_forkchoiceUpdatedV3</c> before the Gloas fork and
    /// <c>engine_forkchoiceUpdatedV4</c> with the node's custody columns from it.
    /// </summary>
    /// <remarks>
    /// No payload attributes are sent. The fork is that of the wall-clock slot: the head's own slot is not
    /// part of this call, and the execution layer accepts V4 for a head of either fork when no attributes
    /// are present. While the node identity is unknown the custody columns are <c>null</c>, which
    /// execution-apis amsterdam.md defines as a CL that provides no custody services.
    /// </remarks>
    /// <exception cref="EngineUnavailableException">The call produced no status; a failure is not SYNCING.</exception>
    public Task<PayloadStatusV1> ForkchoiceUpdated(Hash256 headExecHash, Hash256 safeExecHash, Hash256 finalizedExecHash) =>
        CallEngineAsync("forkchoiceUpdated", _logger.IsInfo ? $"{headExecHash.ToShortString()}, Safe: {safeExecHash.ToShortString()}, Finalized: {finalizedExecHash.ToShortString()}" : null, async () =>
        {
            Interlocked.Increment(ref Metrics.ForkchoiceUpdatedCallsCount);
            ForkchoiceStateV1 state = new(headExecHash, finalizedExecHash, safeExecHash);
            // execution-apis paris.md engine_forkchoiceUpdatedV1 "timeout: 8s"; Task.Run keeps synchronous EL work inside the deadline.
            Task<PayloadStatusV1> pending = Task.Run(() => SendForkchoiceUpdatedAsync(state));
            _pendingForkchoice = pending;
            _ = pending.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return LastForkchoiceStatus = await pending.WaitAsync(ForkchoiceTimeout);
        });

    private async Task<PayloadStatusV1> SendForkchoiceUpdatedAsync(ForkchoiceStateV1 state)
    {
        // V3 stays valid after Amsterdam only without payload attributes: execution-apis amsterdam.md "Osaka API" bounds only the payload timestamp.
        if (clock.CurrentEpoch < spec.GloasForkEpoch)
        {
            detector.ThrowIfStoodDown();
            ResultWrapper<ForkchoiceUpdatedV1Result> v3 = await detector.InnerEngine.engine_forkchoiceUpdatedV3(state);
            return Unwrap(v3.Result, v3.Data?.PayloadStatus, "forkchoiceUpdatedV3");
        }

        // specs/gloas/fork-choice.md notify_forkchoice_updated: custody_columns is the node's custody set.
        BitArray? columns = ToCustodyColumnBits(custody.Current);
        detector.ThrowIfStoodDown();
        ResultWrapper<ForkchoiceUpdatedV1Result> v4 = await detector.InnerEngine.engine_forkchoiceUpdatedV4(state, null, columns);
        return Unwrap(v4.Result, v4.Data?.PayloadStatus, "forkchoiceUpdatedV4");
    }

    /// <summary>The <c>CustodyColumnBits</c> wire form: bit <c>i</c> set when column <c>i</c> is custodied.</summary>
    private static BitArray? ToCustodyColumnBits(NodeColumnCustody? custody)
    {
        if (custody is null) return null;

        BitArray bits = new(Eip7594DasConstants.NumberOfColumns);
        foreach (ulong column in custody.CustodyColumns)
        {
            bits[(int)column] = true;
        }

        return bits;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Bridges the synchronous transition hook onto <see cref="NewPayload"/> for
    /// <see cref="CurrentBlock"/>. It blocks the calling thread until the engine answers, so the caller must not be a
    /// thread-pool thread; the in-process engine call never re-enters the state transition.
    /// SYNCING/ACCEPTED map to <see cref="ExecutionStatus.Optimistic"/> per the spec's
    /// <c>verify_and_notify_new_payload</c>; only INVALID rejects the block.
    /// </remarks>
    public ExecutionStatus NotifyNewPayload(BeaconBlockBody body) => NotifyNewPayload(body, out _);

    /// <inheritdoc/>
    public ExecutionStatus NotifyNewPayload(BeaconBlockBody body, out Hash256? latestValidHash)
    {
        SignedBeaconBlock block = CurrentBlock ?? throw new InvalidOperationException($"{nameof(CurrentBlock)} must be set before running the state transition");
        if (!ReferenceEquals(block.Message?.Body, body))
            throw new InvalidOperationException($"The body being processed does not belong to {nameof(CurrentBlock)}");

        PayloadStatusV1 status = NewPayload(block).GetAwaiter().GetResult();
        latestValidHash = status.LatestValidHash;
        return ToExecutionStatus(status.Status);
    }

    /// <summary>
    /// Submits a Gloas execution payload envelope's payload via <c>engine_newPayloadV5</c> and
    /// returns the execution layer's verdict. Unlike <see cref="NewPayload(SignedBeaconBlock)"/> it
    /// needs no <see cref="CurrentBlock"/>: the envelope carries its own parent beacon block root.
    /// </summary>
    public async Task<PayloadStatusV1> NewPayload(ExecutionPayloadGloas payload, Hash256?[] versionedHashes, Hash256 parentBeaconBlockRoot, ExecutionRequestsGloas executionRequests)
    {
        Interlocked.Increment(ref Metrics.NewPayloadCallsCount);
        long started = Stopwatch.GetTimestamp();
        ExecutionPayloadV4 converted = PayloadConverter.ToExecutionPayloadV4(payload);
        byte[][] requests = PayloadConverter.ToExecutionRequestsList(executionRequests);
        return await CallEngineAsync("newPayloadV5", _logger.IsInfo ? $"{converted.BlockNumber} ({converted.BlockHash?.ToShortString()})" : null, async () =>
        {
            detector.ThrowIfStoodDown();
            ResultWrapper<PayloadStatusV1> result = await detector.InnerEngine.engine_newPayloadV5(converted, versionedHashes, parentBeaconBlockRoot, requests);
            Interlocked.Add(ref Metrics.NewPayloadMillisecondsCount, (ulong)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return UnwrapNewPayload(result.Result, result.Data, "newPayloadV5");
        });
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
        return Unwrap(result, status, method);
    }

    /// <summary>Runs one engine call and records whether it returned a verdict.</summary>
    /// <exception cref="EngineUnavailableException">The call produced no verdict, threw, or a timed-out forkchoice update is still running.</exception>
    /// <exception cref="OperationCanceledException">An external consensus client took over the engine API.</exception>
    private async Task<PayloadStatusV1> CallEngineAsync(string method, string? details, Func<Task<PayloadStatusV1>> call, Hash256? graffiti = null)
    {
        try
        {
            if (_pendingForkchoice is { IsCompleted: false })
                throw new EngineUnavailableException(method, "the previous forkchoiceUpdated call is still running");
            string operation = method.StartsWith("newPayload", StringComparison.Ordinal) ? "New Block" : "ForkChoice";
            if (_logger.IsInfo)
            {
                string graffitiText = graffiti is null ? "" : $" | Graffiti: {graffiti.Bytes.ToCleanUtf8String()}";
                _logger.Info($"Beacon sending {operation}: {details}{graffitiText}");
            }
            long started = Stopwatch.GetTimestamp();
            PayloadStatusV1 status = await call();
            if (_logger.IsInfo) _logger.Info($"Beacon received {operation} result: {status.Status} | {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms");
            _isAvailable = true;
            return status;
        }
        catch (OperationCanceledException) when (detector.HasStoodDown)
        {
            throw;
        }
        catch (EngineUnavailableException)
        {
            _isAvailable = false;
            throw;
        }
        catch (Exception e)
        {
            _isAvailable = false;
            throw new EngineUnavailableException(method, e.Message, e);
        }
    }

    /// <exception cref="EngineUnavailableException">The call produced no status.</exception>
    private PayloadStatusV1 Unwrap(Result result, PayloadStatusV1? status, string method)
    {
        if (result.ResultType == ResultType.Success && status is not null)
        {
            return status;
        }

        if (_logger.IsError) _logger.Error($"In-process engine_{method} call failed: {result.Error}");
        throw new EngineUnavailableException(method, result.Error);
    }
}
