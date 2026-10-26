// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Diagnostics;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Memory;
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
/// worker); local blob recovery reads the execution layer concurrently. A forkchoice
/// update still running past its deadline fails every later call until it finishes.
/// </remarks>
public sealed class EngineDriver(ExternalClDetector detector, ILogManager logManager, SlotClock clock, BeaconChainSpec spec, INodeColumnCustodySource custody, IBlockTree? blockTree = null, GCKeeper? gcKeeper = null) : IEngineDriver
{
    private readonly ILogger _logger = logManager.GetClassLogger<EngineDriver>();
    private volatile bool _isAvailable = true;
    private Task? _pendingForkchoice;
    private bool _hasSentForkchoice;
    private Task<DataColumnSidecar[]>? _pendingLocalColumns;
    private Hash256? _pendingLocalColumnsRoot;

    /// <summary>Whether the consensus head being sent is more than an epoch behind the wall clock; unset, no head is taken as catching up.</summary>
    internal Func<bool>? IsCatchingUp { get; set; }

    /// <summary>Whether the most recent engine call returned a verdict.</summary>
    public bool IsAvailable => _isAvailable;
    /// <summary>How long a caller waits for a forkchoice update before it counts as unavailable.</summary>
    internal TimeSpan ForkchoiceTimeout { get; init; } = TimeSpan.FromSeconds(8);
    /// <summary>How long an import waits for its first local blob recovery attempt before using network columns.</summary>
    internal TimeSpan LocalColumnsTimeout { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>
    /// The block currently being run through the state transition; the orchestrator sets it before
    /// <see cref="StateTransition.StateTransition.Apply"/> so <see cref="NotifyNewPayload"/> can
    /// recover what the body alone does not carry — the EIP-4788 parent beacon block root.
    /// </summary>
    public SignedBeaconBlock? CurrentBlock { get; set; }

    /// <summary>Recovers local columns within the import budget, retaining at most one unfinished recovery for a later retry.</summary>
    /// <remarks>Called serially by the import worker. Timed-out recovery remains tracked for shutdown and owns its eventual engine response.</remarks>
    internal async Task<DataColumnSidecar[]> GetLocalColumns(SignedBeaconBlock block)
    {
        if (block.Message!.Body!.BlobKzgCommitments!.Length == 0) return [];
        Hash256 root = SszRoots.HashTreeRoot(block.Message);
        if (_pendingLocalColumns is { } pending)
        {
            if (!pending.IsCompleted) return [];
            _pendingLocalColumns = null;
            DataColumnSidecar[] recovered = await pending;
            if (_pendingLocalColumnsRoot == root) return recovered;
        }

        // Register before scheduling, so shutdown also drains a recovery still waiting for a thread.
        IDisposable call;
        try
        {
            call = detector.EnterEmbeddedCall("getBlobsV2");
        }
        catch (EngineUnavailableException e)
        {
            if (_logger.IsDebug) _logger.Debug($"Local blob retrieval failed for slot {block.Message.Slot}: {e.Message}");
            return [];
        }
        _pendingLocalColumnsRoot = root;
        Task<DataColumnSidecar[]> recovery = _pendingLocalColumns = Task.Run(async () =>
        {
            using (call)
                return await GetLocalColumnsCore(block);
        });
        _ = recovery.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            DataColumnSidecar[] columns = await recovery.WaitAsync(LocalColumnsTimeout);
            _pendingLocalColumns = null;
            return columns;
        }
        catch (TimeoutException)
        {
            if (_logger.IsDebug) _logger.Debug($"Local blob retrieval for slot {block.Message.Slot} is still running; waiting for network columns");
            return [];
        }
    }

    private async Task<DataColumnSidecar[]> GetLocalColumnsCore(SignedBeaconBlock block)
    {
        if (custody.Current is not { } localCustody) return [];
        SszKzgCommitment[] commitments = block.Message!.Body!.BlobKzgCommitments!;
        if (commitments.Length == 0) return [];
        byte[][] hashes = new byte[commitments.Length][];
        for (int i = 0; i < hashes.Length; i++)
        {
            hashes[i] = new byte[Hash256.Size];
            Nethermind.Crypto.KzgPolynomialCommitments.TryComputeCommitmentHashV1(commitments[i].AsSpan(), hashes[i]);
        }
        try
        {
            ResultWrapper<IReadOnlyList<BlobAndProofV2?>?> result = await detector.InnerEngine.engine_getBlobsV2(hashes);
            using IDisposable? response = result.Data as IDisposable;
            return result.Result.ResultType == ResultType.Success && result.Data is { } blobs ? LocalBlobColumns.Create(block, blobs, localCustody.SampledColumns) : [];
        }
        catch (Exception e) when (e is not OutOfMemoryException and not OperationCanceledException)
        {
            if (_logger.IsDebug) _logger.Debug($"Local blob retrieval failed for slot {block.Message.Slot}: {e.Message}");
            return [];
        }
    }
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
        // EIP-4788: the payload's parent_beacon_block_root is the parent root of the beacon block carrying it.
        Hash256?[] versionedHashes = PayloadConverter.ToBlobVersionedHashes(body.BlobKzgCommitments);
        byte[][] requests = PayloadConverter.ToExecutionRequestsList(body.ExecutionRequests);
        return await CallEngineAsync("newPayloadV4", _logger.IsInfo ? $"{payload.BlockNumber} ({payload.BlockHash?.ToShortString()})" : null, async () =>
        {
            using IDisposable call = detector.EnterEmbeddedCall("newPayloadV4");
            gcKeeper?.CancelPendingGC();
            long started = Stopwatch.GetTimestamp();
            ResultWrapper<PayloadStatusV1> result = await detector.InnerEngine.engine_newPayloadV4(payload, versionedHashes, message.ParentRoot, requests);
            TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
            Interlocked.Add(ref Metrics.NewPayloadMillisecondsCount, (ulong)elapsed.TotalMilliseconds);
            return (Unwrap(result.Result, result.Data, "newPayloadV4"), elapsed);
        }, body.Graffiti, payload.BlockNumber);
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
    /// While <see cref="IsCatchingUp"/>, a head that is a main-chain ancestor of the execution layer's head is sent as that head, with the requested
    /// safe and finalized blocks; at the tip such a head, such as a reorg to the parent or an EMPTY Gloas head's older payload, is sent as is.
    /// </remarks>
    /// <exception cref="EngineUnavailableException">The call produced no status; a failure is not SYNCING.</exception>
    public Task<PayloadStatusV1> ForkchoiceUpdated(Hash256 headExecHash, Hash256 safeExecHash, Hash256 finalizedExecHash)
    {
        bool firstUpdate = !_hasSentForkchoice;
        _hasSentForkchoice = true;
        if (blockTree?.Head?.Header is { } currentHead
            && blockTree.FindHeader(headExecHash, BlockTreeLookupOptions.DoNotCreateLevelIfMissing) is { } requestedHead
            && requestedHead.Number < currentHead.Number && blockTree.IsMainChain(requestedHead))
        {
            if (firstUpdate && headExecHash == safeExecHash && headExecHash == finalizedExecHash)
            {
                return Task.FromResult(new PayloadStatusV1 { Status = PayloadStatus.Valid, LatestValidHash = headExecHash });
            }

            // execution-apis paris.md engine_forkchoiceUpdatedV1: the EL may stay ahead of a VALID ancestor head, so it is not rewound to one.
            if (IsCatchingUp?.Invoke() == true
                && blockTree.FindHeader(finalizedExecHash, BlockTreeLookupOptions.DoNotCreateLevelIfMissing) is { } finalized && finalized.Number <= currentHead.Number)
            {
                if (_logger.IsDebug) _logger.Debug($"Forkchoice head {requestedHead.Number} ({headExecHash.ToShortString()}) is a canonical ancestor of execution head {currentHead.Number}; keeping the execution head");
                headExecHash = currentHead.Hash!;
            }
        }
        ulong? headNumber = _logger.IsInfo ? blockTree?.FindHeader(headExecHash, BlockTreeLookupOptions.DoNotCreateLevelIfMissing)?.Number : null;
        return CallEngineAsync("forkchoiceUpdated", _logger.IsInfo ? $"{(headNumber is { } number ? $"{number} ({headExecHash.ToShortString()})" : headExecHash.ToShortString())}, Safe: {FormatExecutionBlock(safeExecHash)}, Finalized: {FormatExecutionBlock(finalizedExecHash)}" : null, async () =>
        {
            Interlocked.Increment(ref Metrics.ForkchoiceUpdatedCallsCount);
            ForkchoiceStateV1 state = new(headExecHash, finalizedExecHash, safeExecHash);
            // execution-apis paris.md engine_forkchoiceUpdatedV1 "timeout: 8s"; Task.Run keeps synchronous EL work inside the deadline.
            IDisposable call = detector.EnterEmbeddedCall("forkchoiceUpdated");
            Task<(PayloadStatusV1 Status, TimeSpan Elapsed)> pending = Task.Run(async () =>
            {
                using (call)
                    return await SendForkchoiceUpdatedAsync(state);
            });
            _pendingForkchoice = pending;
            _ = pending.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            (PayloadStatusV1 status, TimeSpan elapsed) = await pending.WaitAsync(ForkchoiceTimeout);
            return (status, elapsed);
        }, blockNumber: headNumber);
    }

    private string FormatExecutionBlock(Hash256 hash) =>
        blockTree?.FindHeader(hash, BlockTreeLookupOptions.DoNotCreateLevelIfMissing) is { } header
            ? $"{header.Number} ({hash.ToShortString()})"
            : hash.ToShortString();

    private async Task<(PayloadStatusV1 Status, TimeSpan Elapsed)> SendForkchoiceUpdatedAsync(ForkchoiceStateV1 state)
    {
        // V3 stays valid after Amsterdam only without payload attributes: execution-apis amsterdam.md "Osaka API" bounds only the payload timestamp.
        if (clock.CurrentEpoch < spec.GloasForkEpoch)
        {
            long startedV3 = Stopwatch.GetTimestamp();
            ResultWrapper<ForkchoiceUpdatedV1Result> v3 = await detector.InnerEngine.engine_forkchoiceUpdatedV3(state);
            TimeSpan elapsedV3 = Stopwatch.GetElapsedTime(startedV3);
            return (Unwrap(v3.Result, v3.Data?.PayloadStatus, "forkchoiceUpdatedV3"), elapsedV3);
        }

        // specs/gloas/fork-choice.md notify_forkchoice_updated: custody_columns is the node's custody set.
        BitArray? columns = ToCustodyColumnBits(custody.Current);
        long startedV4 = Stopwatch.GetTimestamp();
        ResultWrapper<ForkchoiceUpdatedV1Result> v4 = await detector.InnerEngine.engine_forkchoiceUpdatedV4(state, null, columns);
        TimeSpan elapsedV4 = Stopwatch.GetElapsedTime(startedV4);
        return (Unwrap(v4.Result, v4.Data?.PayloadStatus, "forkchoiceUpdatedV4"), elapsedV4);
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
        ExecutionPayloadV4 converted = PayloadConverter.ToExecutionPayloadV4(payload);
        byte[][] requests = PayloadConverter.ToExecutionRequestsList(executionRequests);
        return await CallEngineAsync("newPayloadV5", _logger.IsInfo ? $"{converted.BlockNumber} ({converted.BlockHash?.ToShortString()})" : null, async () =>
        {
            using IDisposable call = detector.EnterEmbeddedCall("newPayloadV5");
            gcKeeper?.CancelPendingGC();
            long started = Stopwatch.GetTimestamp();
            ResultWrapper<PayloadStatusV1> result = await detector.InnerEngine.engine_newPayloadV5(converted, versionedHashes, parentBeaconBlockRoot, requests);
            TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
            Interlocked.Add(ref Metrics.NewPayloadMillisecondsCount, (ulong)elapsed.TotalMilliseconds);
            return (Unwrap(result.Result, result.Data, "newPayloadV5"), elapsed);
        }, blockNumber: converted.BlockNumber);
    }

    /// <inheritdoc/>
    /// <remarks>Same mapping as the block-body overload: only INVALID rejects the envelope.</remarks>
    public ExecutionStatus NotifyNewPayload(ExecutionPayloadGloas payload, Hash256?[] versionedHashes, Hash256 parentBeaconBlockRoot, ExecutionRequestsGloas executionRequests) =>
        NotifyNewPayload(payload, versionedHashes, parentBeaconBlockRoot, executionRequests, out _);

    /// <inheritdoc/>
    public ExecutionStatus NotifyNewPayload(ExecutionPayloadGloas payload, Hash256?[] versionedHashes, Hash256 parentBeaconBlockRoot, ExecutionRequestsGloas executionRequests, out Hash256? latestValidHash)
    {
        PayloadStatusV1 status = NewPayload(payload, versionedHashes, parentBeaconBlockRoot, executionRequests).GetAwaiter().GetResult();
        latestValidHash = status.LatestValidHash;
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

    /// <summary>Runs one engine call and records whether it returned a verdict.</summary>
    /// <exception cref="EngineUnavailableException">The call produced no verdict, threw, a timed-out forkchoice update is still running, or an external engine call is in flight.</exception>
    /// <exception cref="OperationCanceledException">An external consensus client took over the engine API.</exception>
    private async Task<PayloadStatusV1> CallEngineAsync(string method, string? details, Func<Task<(PayloadStatusV1 Status, TimeSpan Elapsed)>> call, Hash256? graffiti = null, ulong? blockNumber = null)
    {
        try
        {
            if (_pendingForkchoice is { IsCompleted: false })
                throw new EngineUnavailableException(method, "the previous forkchoiceUpdated call is still running");
            string operation = method.StartsWith("newPayload", StringComparison.Ordinal) ? "New Block" : "ForkChoice";
            if (_logger.IsInfo)
            {
                string graffitiText = graffiti is null ? "" : $"{string.Empty,31}| Graffiti:   {graffiti.Bytes.ToCleanUtf8String()}";
                _logger.Info($"Beacon {operation}:{(operation == "New Block" ? "    " : "   ")}{details}{graffitiText}");
            }
            (PayloadStatusV1 status, TimeSpan elapsed) = await call();
            if (_logger.IsInfo) _logger.Info($"Beacon Received:   {blockNumber?.ToString() ?? "?",10}         | {elapsed.TotalMilliseconds,10:N1} ms  | {operation,-10} {status.Status}");
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
