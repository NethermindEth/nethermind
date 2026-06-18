// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO.Pipelines;
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

public enum ParityTraceStreamMode
{
    Replay,
    Store,
}

public sealed class StreamingParityLikeBlockTracer : ParityLikeBlockTracer, IDisposable
{
    private readonly Utf8JsonWriter _writer;
    private readonly PipeWriter? _pipeWriter;
    private readonly CancellationToken _cancellationToken;
    private readonly ParityTraceTypes _defaultTypes;
    private readonly IDictionary<Hash256, ParityTraceTypes>? _typesByTransaction;
    private readonly ParityTraceStreamMode _mode;
    private readonly bool _includeTxHash;
    private readonly TxTraceFilter? _storeFilter;
    private readonly JsonSerializerOptions _jsonOptions;

    private ParityLikeTxTrace? _pendingRewardTrace;
    private Block? _block;
    private StreamingParityLikeTxTracer? _reusableTxTracer;
    private ParityTraceTypes _reusableTxTracerTypes;

    public StreamingParityLikeBlockTracer(
        ParityTraceTypes types,
        ParityTraceStreamMode mode,
        bool includeTxHash,
        Utf8JsonWriter writer,
        PipeWriter? pipeWriter,
        CancellationToken cancellationToken,
        TxTraceFilter? storeFilter = null)
        : base(types)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _defaultTypes = types;
        _mode = mode;
        _includeTxHash = includeTxHash;
        _writer = writer;
        _pipeWriter = pipeWriter;
        _cancellationToken = cancellationToken;
        _storeFilter = storeFilter;
        _jsonOptions = EthereumJsonSerializer.JsonOptions;
    }

    public StreamingParityLikeBlockTracer(
        Hash256 txHash,
        ParityTraceTypes types,
        ParityTraceStreamMode mode,
        bool includeTxHash,
        Utf8JsonWriter writer,
        PipeWriter? pipeWriter,
        CancellationToken cancellationToken)
        : base(txHash, types)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _defaultTypes = types;
        _mode = mode;
        _includeTxHash = includeTxHash;
        _writer = writer;
        _pipeWriter = pipeWriter;
        _cancellationToken = cancellationToken;
        _jsonOptions = EthereumJsonSerializer.JsonOptions;
    }

    public StreamingParityLikeBlockTracer(
        IDictionary<Hash256, ParityTraceTypes> typesByTransaction,
        ParityTraceTypes defaultTypes,
        ParityTraceStreamMode mode,
        bool includeTxHash,
        Utf8JsonWriter writer,
        PipeWriter? pipeWriter,
        CancellationToken cancellationToken)
        : base(typesByTransaction)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(typesByTransaction);
        _defaultTypes = defaultTypes;
        _typesByTransaction = typesByTransaction;
        _mode = mode;
        _includeTxHash = includeTxHash;
        _writer = writer;
        _pipeWriter = pipeWriter;
        _cancellationToken = cancellationToken;
        _jsonOptions = EthereumJsonSerializer.JsonOptions;
    }

    public override void StartNewBlockTrace(Block block)
    {
        _block = block;
        base.StartNewBlockTrace(block);
    }

    protected override ParityLikeTxTracer OnStart(Transaction? tx)
    {
        _cancellationToken.ThrowIfCancellationRequested();

        ParityTraceTypes resolvedTypes = ResolveTypes(tx);
        bool fillVmTraceSlot = _mode == ParityTraceStreamMode.Replay;

        if (fillVmTraceSlot)
        {
            _writer.WriteStartObject();
            _writer.WritePropertyName("vmTrace"u8);
        }

        if (_reusableTxTracer is null || _reusableTxTracerTypes != resolvedTypes)
        {
            _reusableTxTracer?.ReleaseResources();
            _reusableTxTracer = new StreamingParityLikeTxTracer(
                _block!, tx, resolvedTypes, _writer, _pipeWriter, _cancellationToken, fillVmTraceSlot);
            _reusableTxTracerTypes = resolvedTypes;
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

        if (IsTracingRewards && trace.TransactionHash is null && trace.Action is null)
        {
            _pendingRewardTrace = trace;
            return;
        }

        EmitTrace(trace);
        FlushPipe();
    }

    public override void ReportReward(Address author, string rewardType, UInt256 rewardValue)
    {
        if (_pendingRewardTrace is null) return;

        _pendingRewardTrace.Action = new ParityTraceAction
        {
            RewardType = rewardType,
            Value = rewardValue,
            Author = author,
            CallType = "reward",
            TraceAddress = CappedArray<int>.Empty,
            Type = "reward",
            Result = null
        };

        EmitTrace(_pendingRewardTrace);
        _pendingRewardTrace.Action = null;
        _pendingRewardTrace = null;
        FlushPipe();
    }

    public override void EndBlockTrace()
    {
        _reusableTxTracer?.ReleaseResources();
        _reusableTxTracer = null;
        base.EndBlockTrace();
    }

    public void Dispose()
    {
        _reusableTxTracer?.ReleaseResources();
        _reusableTxTracer = null;
    }

    private ParityTraceTypes ResolveTypes(Transaction? tx) =>
        tx is not null && _typesByTransaction is not null && _typesByTransaction.TryGetValue(tx.Hash!, out ParityTraceTypes perTxTypes)
            ? perTxTypes
            : _defaultTypes;

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

    private void EmitReplayEnvelopeTail(ParityLikeTxTrace trace) =>
        ParityReplayEnvelopeWriter.WriteTail(_writer, trace, _includeTxHash, _jsonOptions);

    private void EmitStoreItems(ParityLikeTxTrace trace)
    {
        foreach (ParityTxTraceFromStore item in ParityTxTraceFromStore.FromTxTrace(trace))
        {
            if (_storeFilter is not null && !_storeFilter.ShouldUseTxTrace(item.Action)) continue;
            JsonSerializer.Serialize(_writer, item, _jsonOptions);
        }
    }

    private void FlushPipe()
    {
        if (_pipeWriter is null) return;
        _writer.Flush();
        _pipeWriter.FlushAsync(_cancellationToken).SafeWait();
    }
}
