// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.Modules.Trace;

/// <summary>
/// Output shape selector for <see cref="StreamingParityLikeBlockTracer"/>:
/// <see cref="Replay"/> produces one <c>ParityTxTraceFromReplay</c> envelope per transaction;
/// <see cref="Store"/> produces a flat sequence of <c>ParityTxTraceFromStore</c> action records.
/// </summary>
public enum ParityTraceStreamMode
{
    Replay,
    Store,
}

/// <summary>
/// Block tracer that writes parity tx traces straight to the response
/// <see cref="Utf8JsonWriter"/> as each transaction completes, with the per-tx vmTrace
/// streamed per-opcode through a <see cref="StreamingParityLikeTxTracer"/>. The full
/// per-tx <see cref="ParityLikeTxTrace"/> is never accumulated into a block-wide list,
/// so peak heap is bounded by the largest single transaction's action tree / state diff
/// (typically tens of KB) regardless of block size.
/// </summary>
/// <remarks>
/// <para>
/// <b>Field order:</b> In <see cref="ParityTraceStreamMode.Replay"/> mode the streamed
/// envelope writes <c>"vmTrace"</c> before <c>"output"</c>/<c>"stateDiff"</c>/<c>"trace"</c>/
/// <c>"transactionHash"</c>, because the vmTrace value has to be opened before EVM
/// execution begins so opcodes can be emitted per-instruction. The non-streaming buffered
/// path emits these fields in <c>output, stateDiff, trace, transactionHash, vmTrace</c>
/// order. Clients that parse by field name are unaffected; byte-for-byte comparators
/// against the buffered path will see the difference. Mirrors the same trade-off
/// accepted by the Geth streaming tracer (PR #11693).
/// </para>
/// <para>
/// <b>Reward placeholders:</b> The block processor emits reward "transactions" via
/// <c>StartNewTxTrace(null)</c> / <c>EndTxTrace</c> followed by <c>ReportReward</c>. We
/// defer JSON emission for those tx traces until <see cref="ReportReward"/> populates
/// the placeholder's <c>Action</c>; the in-memory hold-back is exactly one trace.
/// </para>
/// </remarks>
public sealed class StreamingParityLikeBlockTracer : ParityLikeBlockTracer
{
    private readonly Utf8JsonWriter _writer;
    private readonly PipeWriter? _pipeWriter;
    private readonly CancellationToken _cancellationToken;
    private readonly ParityTraceTypes _types;
    private readonly IDictionary<Hash256, ParityTraceTypes>? _typesByTransaction;
    private readonly ParityTraceStreamMode _mode;
    private readonly bool _includeTxHash;
    private readonly StoreItemPredicate? _storeFilter;
    private readonly JsonSerializerOptions _jsonOptions;

    // Reward placeholders are added via StartNewTxTrace(null) / EndTxTrace, then ReportReward
    // is called to populate the Action. We defer JSON emission until ReportReward arrives.
    private ParityLikeTxTrace? _pendingRewardTrace;
    private Block? _block;

    // A single reusable tx tracer for the whole block: every action/vm-trace stack,
    // push list, streaming-frame pool, and per-opcode buffer it owns is allocated once
    // and reused across all txs (and reward placeholders) in this block.
    private StreamingParityLikeTxTracer? _reusableTxTracer;

    /// <summary>
    /// Predicate applied to each action in <see cref="ParityTraceStreamMode.Store"/> mode.
    /// Items returning <c>false</c> are dropped; this is how <c>trace_filter</c> applies
    /// its address / after / count filter without materialising the full action list.
    /// </summary>
    public delegate bool StoreItemPredicate(ParityTraceAction action);

    public StreamingParityLikeBlockTracer(
        ParityTraceTypes types,
        ParityTraceStreamMode mode,
        bool includeTxHash,
        Utf8JsonWriter writer,
        PipeWriter? pipeWriter,
        CancellationToken cancellationToken,
        StoreItemPredicate? storeFilter = null)
        : base(types)
    {
        ArgumentNullException.ThrowIfNull(writer);

        _types = types;
        _mode = mode;
        _includeTxHash = includeTxHash;
        _writer = writer;
        _pipeWriter = pipeWriter;
        _cancellationToken = cancellationToken;
        _storeFilter = storeFilter;
        _jsonOptions = EthereumJsonSerializer.JsonOptions;
    }

    /// <summary>
    /// Per-tx trace-types constructor used by <c>trace_callMany</c>. <paramref name="defaultTypes"/>
    /// is the fallback applied when a tx hash is missing from <paramref name="typesByTransaction"/>.
    /// </summary>
    public StreamingParityLikeBlockTracer(
        IDictionary<Hash256, ParityTraceTypes> typesByTransaction,
        ParityTraceTypes defaultTypes,
        ParityTraceStreamMode mode,
        bool includeTxHash,
        Utf8JsonWriter writer,
        PipeWriter? pipeWriter,
        CancellationToken cancellationToken,
        StoreItemPredicate? storeFilter = null)
        : base(typesByTransaction)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(typesByTransaction);

        _types = defaultTypes;
        _typesByTransaction = typesByTransaction;
        _mode = mode;
        _includeTxHash = includeTxHash;
        _writer = writer;
        _pipeWriter = pipeWriter;
        _cancellationToken = cancellationToken;
        _storeFilter = storeFilter;
        _jsonOptions = EthereumJsonSerializer.JsonOptions;
    }

    public override void StartNewBlockTrace(Block block)
    {
        _block = block;
        base.StartNewBlockTrace(block);
    }

    protected override ParityLikeTxTracer OnStart(Transaction? tx)
    {
        ParityTraceTypes resolvedTypes =
            tx is not null && _typesByTransaction is not null && _typesByTransaction.TryGetValue(tx.Hash!, out ParityTraceTypes perTxTypes)
                ? perTxTypes
                : _types;

        bool fillVmTraceSlot = _mode == ParityTraceStreamMode.Replay;
        if (fillVmTraceSlot)
        {
            // Open the Replay envelope and reserve the vmTrace slot before the tracer is
            // constructed; the StreamingParityLikeTxTracer streams into that slot during
            // execution (or writes null if vmTrace wasn't requested).
            _cancellationToken.ThrowIfCancellationRequested();
            _writer.WriteStartObject();
            _writer.WritePropertyName("vmTrace"u8);
        }

        // In Store mode no field competes with actions for the writer during execution,
        // so we can emit each action's JSON directly at PopAction (post-order) and never
        // hold the action tree in memory. Replay mode keeps the buffered tree because
        // vmTrace owns the writer during execution.
        bool streamActionsInline = !fillVmTraceSlot;
        Func<ParityTraceAction, bool>? actionFilter = _storeFilter is null ? null : new(_storeFilter);

        // Reuse the tx tracer across every tx (and reward placeholder) in this block.
        // Per-tx trace-types can vary in callMany; if they differ from the cached tracer
        // we allocate a new one (rare — typically all txs share the same types).
        if (_reusableTxTracer is null || _reusableTxTracer.ParityTraceTypes != resolvedTypes)
        {
            _reusableTxTracer = new StreamingParityLikeTxTracer(_block!, tx, resolvedTypes, _writer, fillVmTraceSlot, streamActionsInline, actionFilter);
        }
        else
        {
            _reusableTxTracer.ResetForNextTx(_block!, tx);
        }
        return _reusableTxTracer;
    }

    protected override ParityLikeTxTrace OnEnd(ParityLikeTxTracer txTracer) => txTracer.BuildResult();

    protected override void AddTrace(ParityLikeTxTrace trace)
    {
        _cancellationToken.ThrowIfCancellationRequested();

        // Defer reward placeholders: ReportReward will populate Action and emit them.
        if (IsTracingRewards && trace.TransactionHash is null && trace.Action is null)
        {
            _pendingRewardTrace = trace;
            return;
        }

        // Store mode with inline action streaming: the tx tracer already emitted every
        // action's JSON at its PopAction; nothing left to do here beyond flushing.
        if (_mode == ParityTraceStreamMode.Store)
        {
            FlushPipe();
            return;
        }

        EmitTrace(trace);
        FlushPipe();
    }

    public override void ReportReward(Address author, string rewardType, UInt256 rewardValue)
    {
        if (_pendingRewardTrace is null) return;

        ParityTraceAction rewardAction = new()
        {
            RewardType = rewardType,
            Value = rewardValue,
            Author = author,
            CallType = "reward",
            TraceAddress = CappedArray<int>.Empty,
            Type = "reward",
            Result = null,
        };
        _pendingRewardTrace.Action = rewardAction;

        if (_mode == ParityTraceStreamMode.Store)
        {
            // Store mode never went through the per-action inline emit path for this
            // placeholder (it had no PushAction/PopAction lifecycle), so emit the reward
            // action's JSON ourselves now. The per-tx tracer's PopAction loop has finished.
            if (_storeFilter is null || _storeFilter(rewardAction))
            {
                StreamingParityLikeTxTracer.WriteStoreActionJson(_writer, rewardAction, _pendingRewardTrace, _jsonOptions);
            }
        }
        else
        {
            EmitTrace(_pendingRewardTrace);
        }
        _pendingRewardTrace = null;
        FlushPipe();
    }

    private void EmitTrace(ParityLikeTxTrace trace)
    {
        if (_mode == ParityTraceStreamMode.Replay)
        {
            EmitReplayEnvelopeTail(trace);
        }
        else
        {
            EmitStoreItems(trace);
        }
    }

    private void EmitReplayEnvelopeTail(ParityLikeTxTrace trace)
    {
        // OnStart wrote `{"vmTrace":` and the tx tracer streamed the vmTrace value during
        // execution. Now write the remaining envelope fields and close.
        _writer.WritePropertyName("output"u8);
        JsonSerializer.Serialize(_writer, trace.Output, _jsonOptions);

        _writer.WritePropertyName("stateDiff"u8);
        if (trace.StateChanges is not null)
        {
            _writer.WriteStartObject();
            Span<byte> addressBytes = stackalloc byte[Address.Size * 2 + 2];
            addressBytes[0] = (byte)'0';
            addressBytes[1] = (byte)'x';
            Span<byte> hex = addressBytes[2..];
            foreach ((Address address, ParityAccountStateChange stateChange) in
                trace.StateChanges.OrderBy(static sc => sc.Key, GenericComparer.GetOptimized<Address>()))
            {
                address.Bytes.OutputBytesToByteHex(hex, false);
                _writer.WritePropertyName(addressBytes);
                JsonSerializer.Serialize(_writer, stateChange, _jsonOptions);
            }
            _writer.WriteEndObject();
        }
        else
        {
            _writer.WriteNullValue();
        }

        _writer.WritePropertyName("trace"u8);
        _writer.WriteStartArray();
        if (trace.Action is not null)
        {
            WriteActionRecursively(trace.Action);
        }
        _writer.WriteEndArray();

        if (_includeTxHash)
        {
            _writer.WritePropertyName("transactionHash"u8);
            JsonSerializer.Serialize(_writer, trace.TransactionHash, _jsonOptions);
        }

        _writer.WriteEndObject();
    }

    private void WriteActionRecursively(ParityTraceAction action)
    {
        // Matches ParityTraceActionFromReplayJsonConverter's pre-order traversal: each
        // action emits a flat object, then each subtrace emits its own flat object.
        if (!action.IncludeInTrace) return;

        _writer.WriteStartObject();

        _writer.WritePropertyName("action"u8);
        ParityTraceActionConverter.Instance.Write(_writer, action, _jsonOptions);

        if (action.Error is null)
        {
            _writer.WritePropertyName("result"u8);
            JsonSerializer.Serialize(_writer, action.Result, _jsonOptions);
        }
        else
        {
            _writer.WritePropertyName("error"u8);
            JsonSerializer.Serialize(_writer, action.Error, _jsonOptions);
        }

        _writer.WriteNumber("subtraces"u8, action.IncludedSubtraceCount);

        _writer.WritePropertyName("traceAddress"u8);
        JsonSerializer.Serialize(_writer, action.TraceAddress, _jsonOptions);

        _writer.WriteString("type"u8, action.Type);
        _writer.WriteEndObject();

        for (int i = 0; i < action.Subtraces.Count; i++)
        {
            WriteActionRecursively(action.Subtraces[i]);
        }
    }

    private void EmitStoreItems(ParityLikeTxTrace trace)
    {
        // Dead code under post-order inline streaming for Store mode — kept as the fallback
        // path used when the tx tracer for some reason did not emit inline (today only
        // executes for the legacy Replay->Store path, but worth keeping symmetric).
        foreach (ParityTxTraceFromStore item in ParityTxTraceFromStore.FromTxTrace(trace))
        {
            if (_storeFilter is not null && !_storeFilter(item.Action))
            {
                continue;
            }

            JsonSerializer.Serialize(_writer, item, _jsonOptions);
        }
    }

    private void FlushPipe()
    {
        if (_pipeWriter is null) return;
        _writer.Flush();
        _pipeWriter.FlushAsync(_cancellationToken).GetAwaiter().GetResult();
    }
}
