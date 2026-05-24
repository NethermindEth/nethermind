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
/// <see cref="Replay"/> emits one <c>ParityTxTraceFromReplay</c> envelope per tx;
/// <see cref="Store"/> emits a flat sequence of <c>ParityTxTraceFromStore</c> records.
/// </summary>
public enum ParityTraceStreamMode
{
    Replay,
    Store,
}

/// <summary>
/// Block tracer that writes parity tx traces straight to the response
/// <see cref="Utf8JsonWriter"/> as each transaction completes, with the per-tx vmTrace
/// streamed per-opcode. Peak heap is bounded by the largest single transaction.
/// </summary>
/// <remarks>
/// <b>Field order:</b> In Replay mode the envelope writes <c>"vmTrace"</c> before the
/// remaining fields because the vmTrace slot has to be opened before EVM execution.
/// Buffered output orders the fields differently; clients that parse by name are
/// unaffected. Mirrors the trade-off accepted by the Geth streaming tracer (#11693).
/// <para>
/// Reward placeholders arrive as <c>StartNewTxTrace(null) / EndTxTrace</c> followed by
/// <c>ReportReward</c>; emission is deferred until <see cref="ReportReward"/> populates
/// the placeholder's Action. The in-memory hold-back is exactly one trace.
/// </para>
/// </remarks>
public sealed class StreamingParityLikeBlockTracer : ParityLikeBlockTracer, IDisposable
{
    private readonly Utf8JsonWriter _writer;
    private readonly PipeWriter? _pipeWriter;
    private readonly CancellationToken _cancellationToken;
    private readonly ParityTraceTypes _types;
    private readonly IDictionary<Hash256, ParityTraceTypes>? _typesByTransaction;
    private readonly ParityTraceStreamMode _mode;
    private readonly bool _includeTxHash;
    private readonly StoreItemPredicate? _storeFilter;
    private readonly Func<ParityTraceAction, bool>? _cachedActionFilter;
    private readonly JsonSerializerOptions _jsonOptions;

    private ParityLikeTxTrace? _pendingRewardTrace;
    private Block? _block;

    // Single reusable tx tracer for the whole block; all pooled state is reused across txs.
    private StreamingParityLikeTxTracer? _reusableTxTracer;

    /// <summary>
    /// Per-action predicate in Store mode; items returning <c>false</c> are dropped.
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
        _cachedActionFilter = storeFilter is null ? null : new(storeFilter);
        _jsonOptions = EthereumJsonSerializer.JsonOptions;
    }

    /// <summary>
    /// Per-tx trace-types constructor for <c>trace_callMany</c>. <paramref name="defaultTypes"/>
    /// is the fallback when a tx hash is missing from <paramref name="typesByTransaction"/>.
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
        _cachedActionFilter = storeFilter is null ? null : new(storeFilter);
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

        _cancellationToken.ThrowIfCancellationRequested();

        bool fillVmTraceSlot = _mode == ParityTraceStreamMode.Replay;
        if (fillVmTraceSlot)
        {
            // Open the Replay envelope and reserve the vmTrace slot before tracer construction;
            // the tx tracer streams into that slot during execution.
            _writer.WriteStartObject();
            _writer.WritePropertyName("vmTrace"u8);
        }

        // Store mode can emit each action at PopAction (post-order) since no field competes
        // for the writer; Replay must keep the action tree buffered because vmTrace owns it.
        bool streamActionsInline = !fillVmTraceSlot;

        // Reuse the tx tracer across every tx in this block. callMany may vary per-tx
        // types — dispose the prior instance before replacing so its ArrayPool rentals
        // don't leak.
        if (_reusableTxTracer is null || _reusableTxTracer.ParityTraceTypes != resolvedTypes)
        {
            _reusableTxTracer?.Dispose();
            _reusableTxTracer = new StreamingParityLikeTxTracer(_block!, tx, resolvedTypes, _writer, fillVmTraceSlot, streamActionsInline, _cachedActionFilter);
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

        // Reward placeholder: defer until ReportReward populates Action.
        if (IsTracingRewards && trace.TransactionHash is null && trace.Action is null)
        {
            _pendingRewardTrace = trace;
            return;
        }

        // Store mode: tx tracer already emitted every action at PopAction.
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
            // Reward placeholder had no PushAction/PopAction lifecycle, so emit it ourselves.
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
        // OnStart wrote `{"vmTrace":` and the tx tracer streamed its value.
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

    // Pre-order traversal matching ParityTraceActionFromReplayJsonConverter.
    private void WriteActionRecursively(ParityTraceAction action)
    {
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

    // Fallback path: only reached if the tx tracer did not emit actions inline.
    private void EmitStoreItems(ParityLikeTxTrace trace)
    {
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
        // Sync-over-async: JSON-RPC runs without a SynchronizationContext (matches #11693).
        _pipeWriter.FlushAsync(_cancellationToken).GetAwaiter().GetResult();
    }

    public override void EndBlockTrace()
    {
        _reusableTxTracer?.Dispose();
        base.EndBlockTrace();
    }

    /// <summary>
    /// Returns every pool-rented buffer held by the inner tx tracer. Safe after a
    /// cancelled / failed run; otherwise those buffers leak from the pool.
    /// </summary>
    public void Dispose()
    {
        _reusableTxTracer?.Dispose();
        _reusableTxTracer = null;
    }
}
