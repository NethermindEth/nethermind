// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Nethermind.Blockchain.Tracing.ParityStyle;

/// <summary>
/// Streams the <c>vmTrace</c> portion of a parity-style trace straight to a
/// <see cref="Utf8JsonWriter"/> as opcodes execute, dropping vmTrace peak heap from
/// O(opcodes) to O(call-depth).
/// </summary>
/// <remarks>
/// Action tree, state-diff and output remain buffered: their JSON shape requires
/// parent-known fields (subtraces count, pre-order child enumeration) before children
/// are knowable. They are bounded by call-depth × subtraces.
/// <para>
/// The streamed <c>vmTrace</c> value is written into a pre-positioned slot: the caller
/// writes <c>"vmTrace":</c> immediately before constructing this tracer. If vmTrace was
/// not requested, the constructor writes <c>null</c> at that position.
/// </para>
/// </remarks>
public class StreamingParityLikeTxTracer : ParityLikeTxTracer
{
    private readonly Utf8JsonWriter _writer;
    private readonly bool _streamVmTrace;
    private readonly JsonSerializerOptions _jsonOptions;

    private readonly Stack<StreamingVmFrame> _streamingFrames = new();
    private readonly Stack<StreamingVmFrame> _framePool = new();

    // Single in-flight op buffer: only one op is "live" at a time, so we mutate it in
    // place instead of allocating per opcode.
    private ParityVmOperationTrace? _opBuffer;

    // Per-opcode payload buffers, owned by the current op until it is emitted.
    private PooledByteBuffer _memoryData;
    private long _memoryOffset;
    private PooledByteBuffer _storageKey;
    private PooledByteBuffer _storageValue;
    private readonly List<PooledByteBuffer> _streamingPushList = [];

    private readonly Stack<ParityTraceAction> _actionPool = new();
    private readonly Stack<ParityAccountStateChange> _accountStateChangePool = new();
    private readonly Stack<Dictionary<UInt256, ParityStateChange<byte[]>>> _storageDictPool = new();
    private readonly Stack<ParityStateChange<byte[]>> _byteStateChangePool = new();
    private readonly Stack<ParityStateChange<UInt256?>> _uint256StateChangePool = new();

    /// <summary>
    /// Creates a streaming tx tracer.
    /// </summary>
    /// <param name="fillVmTraceSlot">
    /// <see langword="true"/> when the caller has written <c>"vmTrace":</c> and expects
    /// this tracer to populate that slot (stream per-opcode or write <c>null</c>).
    /// <see langword="false"/> when no vmTrace slot is open — tracer behaves like the
    /// base buffered tracer.
    /// </param>
    /// <param name="streamActionsInline">
    /// When <see langword="true"/>, each action's JSON (in <c>ParityTxTraceFromStore</c>
    /// shape) is emitted at <see cref="PopAction"/> in post-order, dropping the per-tx
    /// action-tree buffer. Only valid when <paramref name="fillVmTraceSlot"/> is
    /// <see langword="false"/>.
    /// </param>
    /// <param name="actionFilter">
    /// Optional emit-time predicate; returning <see langword="false"/> drops the action
    /// from the output stream.
    /// </param>
    public StreamingParityLikeTxTracer(
        Block block,
        Transaction? tx,
        ParityTraceTypes parityTraceTypes,
        Utf8JsonWriter writer,
        bool fillVmTraceSlot,
        bool streamActionsInline = false,
        Func<ParityTraceAction, bool>? actionFilter = null)
        : base(block, tx, parityTraceTypes)
    {
        _writer = writer;
        _fillVmTraceSlot = fillVmTraceSlot;
        _streamVmTrace = fillVmTraceSlot && IsTracingInstructions;
        _streamActionsInline = streamActionsInline;
        _actionFilter = actionFilter;
        _jsonOptions = EthereumJsonSerializer.JsonOptions;

        if (fillVmTraceSlot && !IsTracingInstructions)
        {
            _writer.WriteNullValue();
        }
    }

    private readonly bool _fillVmTraceSlot;
    private readonly bool _streamActionsInline;
    private readonly Func<ParityTraceAction, bool>? _actionFilter;

    public ParityTraceTypes ParityTraceTypes => _parityTraceTypes;

    /// <summary>
    /// Re-inits this tracer for the next transaction, keeping every pooled allocation alive.
    /// </summary>
    public void ResetForNextTx(Block block, Transaction? tx)
    {
        if (_trace?.Action is not null)
        {
            ReturnActionTree(_trace.Action);
        }

        if (_trace?.StateChanges is not null)
        {
            ReturnStateChanges(_trace.StateChanges);
        }

        ResetTracerState(block, tx);

        // Defensive drain — a cancelled tx may have left in-flight state.
        while (_streamingFrames.Count > 0)
        {
            ReturnFrame(_streamingFrames.Pop());
        }
        ReleaseOpBuffers();

        if (_fillVmTraceSlot && !IsTracingInstructions)
        {
            _writer.WriteNullValue();
        }
    }

    private sealed class StreamingVmFrame
    {
        // SELFDESTRUCT frame: no JSON emitted; PopVmTraceFrame just pops.
        public bool IsSuicide;
        public bool JsonObjectOpened;
        public byte[]? Code;
        // Parent op's opening was written without closing `}`; this frame owes the brace on return.
        public bool HasPendingParentOpToClose;

        public void Reset()
        {
            IsSuicide = false;
            JsonObjectOpened = false;
            Code = null;
            HasPendingParentOpToClose = false;
        }
    }

    private StreamingVmFrame RentFrame()
    {
        if (_framePool.Count > 0)
        {
            StreamingVmFrame f = _framePool.Pop();
            f.Reset();
            return f;
        }
        return new StreamingVmFrame();
    }

    private void ReturnFrame(StreamingVmFrame frame) => _framePool.Push(frame);

    protected override ParityTraceAction RentAction()
    {
        if (_actionPool.Count == 0) return new ParityTraceAction();
        ParityTraceAction a = _actionPool.Pop();
        a.Reset();
        return a;
    }

    protected override ParityAccountStateChange RentAccountStateChange()
    {
        if (_accountStateChangePool.Count == 0) return new ParityAccountStateChange();
        ParityAccountStateChange a = _accountStateChangePool.Pop();
        a.Code = null;
        a.Balance = null;
        a.Nonce = null;
        a.Storage = null;
        return a;
    }

    protected override Dictionary<UInt256, ParityStateChange<byte[]>> RentStorageDictionary()
    {
        if (_storageDictPool.Count == 0) return [];
        Dictionary<UInt256, ParityStateChange<byte[]>> d = _storageDictPool.Pop();
        d.Clear();
        return d;
    }

    protected override CappedArray<int> RentTraceAddress(int length)
    {
        if (length == 0) return CappedArray<int>.Empty;
        int[] buffer = System.Buffers.ArrayPool<int>.Shared.Rent(length);
        return new CappedArray<int>(buffer, length);
    }

    protected override ParityStateChange<byte[]> RentByteStateChange(byte[] before, byte[] after)
    {
        if (_byteStateChangePool.Count == 0) return new ParityStateChange<byte[]>(before, after);
        ParityStateChange<byte[]> sc = _byteStateChangePool.Pop();
        sc.Before = before;
        sc.After = after;
        return sc;
    }

    protected override ParityStateChange<UInt256?> RentNullableUInt256StateChange(UInt256? before, UInt256? after)
    {
        if (_uint256StateChangePool.Count == 0) return new ParityStateChange<UInt256?>(before, after);
        ParityStateChange<UInt256?> sc = _uint256StateChangePool.Pop();
        sc.Before = before;
        sc.After = after;
        return sc;
    }

    private static void ReturnTraceAddress(CappedArray<int> addr)
    {
        // Skip Empty / default sentinels — they own no rented memory.
        if (addr.IsNotNull && addr.UnderlyingLength > 0)
        {
            System.Buffers.ArrayPool<int>.Shared.Return(addr.UnderlyingArray!);
        }
    }

    /// <summary>
    /// Returns every pool-rented buffer this tracer still holds. Safe after an
    /// interrupted run (timeout / client cancel) so ArrayPool buffers do not leak.
    /// </summary>
    public override void Dispose()
    {
        ReleaseOpBuffers();

        while (_streamingFrames.Count > 0)
        {
            ReturnFrame(_streamingFrames.Pop());
        }

        if (_trace?.Action is not null)
        {
            ReturnActionTree(_trace.Action);
            _trace.Action = null;
        }

        if (_trace?.StateChanges is not null)
        {
            ReturnStateChanges(_trace.StateChanges);
        }
    }

    private void ReturnStateChanges(Dictionary<Address, ParityAccountStateChange> changes)
    {
        foreach (ParityAccountStateChange account in changes.Values)
        {
            if (account.Storage is not null)
            {
                foreach (KeyValuePair<UInt256, ParityStateChange<byte[]>> kv in account.Storage)
                {
                    _byteStateChangePool.Push(kv.Value);
                }
                account.Storage.Clear();
                _storageDictPool.Push(account.Storage);
            }
            if (account.Balance is not null) _uint256StateChangePool.Push(account.Balance);
            if (account.Code is not null) _byteStateChangePool.Push(account.Code);
            if (account.Nonce is not null) _uint256StateChangePool.Push(account.Nonce);

            account.Storage = null;
            account.Balance = null;
            account.Code = null;
            account.Nonce = null;
            _accountStateChangePool.Push(account);
        }
    }

    private void ReturnActionTree(ParityTraceAction action)
    {
        List<ParityTraceAction> subtraces = action.Subtraces;
        for (int i = 0; i < subtraces.Count; i++)
        {
            ReturnActionTree(subtraces[i]);
        }
        subtraces.Clear();
        ReturnTraceAddress(action.TraceAddress);
        action.TraceAddress = default;
        _actionPool.Push(action);
    }

    // Inline mode never uses the subtraces list; PopAction emits the action and the
    // parent's IncludedSubtraceCount (maintained by base PushAction) carries the count.
    protected override void RegisterSubtrace(ParityTraceAction parent, ParityTraceAction child)
    {
        if (_streamActionsInline) return;
        base.RegisterSubtrace(parent, child);
    }

    public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
    {
        // Inline mode: root action was emitted at PopAction and is pooled. _trace.Output
        // belongs to the Replay envelope, not Store.
        if (_streamActionsInline) return;
        base.MarkAsSuccess(recipient, gasSpent, output, logs, stateRoot);
    }

    public override void MarkAsFailed(Address recipient, in GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null)
    {
        if (_streamActionsInline)
        {
            // Quick-fail: tx rejected before any PushAction. Emit a minimal failure action
            // so the user still sees the failed call.
            if (_trace.Action is null && _tx is not null)
            {
                ParityTraceAction action = RentAction();
                action.From = _tx.SenderAddress;
                action.To = _tx.To;
                action.Value = _tx.Value;
                action.Input = _tx.Data.AsArray();
                action.Gas = _tx.GasLimit;
                action.CallType = _tx.IsMessageCall ? "call" : "init";
                action.Error = error;
                action.TraceAddress = CappedArray<int>.Empty;

                if (action.IncludeInTrace && (_actionFilter is null || _actionFilter(action)))
                {
                    WriteStoreActionJson(_writer, action, _trace, _jsonOptions);
                }
                _actionPool.Push(action);
            }
            return;
        }
        base.MarkAsFailed(recipient, gasSpent, output, error, stateRoot);
    }

    // Post-order emit: children have already emitted (so IncludedSubtraceCount is final)
    // and Result/Error have just been set by the closing ReportActionEnd/ReportActionError.
    protected override void OnActionPopped(ParityTraceAction action)
    {
        if (!_streamActionsInline) return;

        if (action.IncludeInTrace && (_actionFilter is null || _actionFilter(action)))
        {
            WriteStoreActionJson(_writer, action, _trace, _jsonOptions);
        }

        ReturnTraceAddress(action.TraceAddress);
        action.TraceAddress = default;
        _actionPool.Push(action);
    }

    /// <summary>
    /// Writes a single action in the <c>ParityTxTraceFromStore</c> JSON shape.
    /// Property order matches the buffered <c>JsonSerializer.Serialize</c> path over
    /// <see cref="Nethermind.JsonRpc.Modules.Trace.ParityTxTraceFromStore"/>.
    /// </summary>
    public static void WriteStoreActionJson(Utf8JsonWriter writer, ParityTraceAction action, ParityLikeTxTrace trace, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        writer.WritePropertyName("action"u8);
        ParityTraceActionConverter.Instance.Write(writer, action, options);

        writer.WritePropertyName("blockHash"u8);
        JsonSerializer.Serialize(writer, trace.BlockHash, options);

        writer.WritePropertyName("blockNumber"u8);
        writer.WriteNumberValue(trace.BlockNumber);

        // Matches the default JsonSerializer behavior (WhenWritingNull ignore condition):
        // reward actions explicitly null out Result without setting Error, and we want
        // neither field to appear in their JSON. Normal actions always have one of the two.
        if (action.Error is not null)
        {
            writer.WriteString("error"u8, action.Error);
        }
        else if (action.Result is not null)
        {
            writer.WritePropertyName("result"u8);
            JsonSerializer.Serialize(writer, action.Result, options);
        }

        writer.WriteNumber("subtraces"u8, action.IncludedSubtraceCount);

        writer.WritePropertyName("traceAddress"u8);
        JsonSerializer.Serialize(writer, action.TraceAddress, options);

        if (trace.TransactionHash is not null)
        {
            writer.WritePropertyName("transactionHash"u8);
            JsonSerializer.Serialize(writer, trace.TransactionHash, options);
        }

        if (trace.TransactionPosition.HasValue)
        {
            writer.WriteNumber("transactionPosition"u8, trace.TransactionPosition.Value);
        }

        writer.WriteString("type"u8, action.Type);

        writer.WriteEndObject();
    }

    protected override void PushVmTraceFrame(ParityTraceAction action)
    {
        StreamingVmFrame frame = RentFrame();

        if (action.Type == "suicide")
        {
            // Suicide sub-trees are omitted from the JSON; keep the stack symmetric with
            // a marker frame so PopVmTraceFrame matches up.
            frame.IsSuicide = true;
            _streamingFrames.Push(frame);
            return;
        }

        if (_currentOperation is not null)
        {
            // Parent op triggers a sub-frame: emit its opening and remember we owe a `}`
            // on frame return. Release the parent op's pooled buffers — sub-frame opcodes
            // will reuse the slots.
            WriteOperationOpening(_currentOperation);
            frame.HasPendingParentOpToClose = true;
            ReleaseOpBuffers();
        }

        _streamingFrames.Push(frame);
        _currentOperation = null;
    }

    protected override void PopVmTraceFrame()
    {
        StreamingVmFrame frame = _streamingFrames.Pop();

        if (frame.IsSuicide)
        {
            ReturnFrame(frame);
            if (_actionStack.Peek().Type != "suicide")
            {
                _treatGasParityStyle = true;
            }
            return;
        }

        // Emit any op left buffered in this frame with an implicit null sub.
        if (_currentOperation is not null)
        {
            WriteOperationOpening(_currentOperation);
            _writer.WriteNullValue();
            _writer.WriteEndObject();
            _currentOperation = null;
            ReleaseOpBuffers();
        }

        if (frame.JsonObjectOpened)
        {
            _writer.WriteEndArray();
            _writer.WriteEndObject();
        }
        else
        {
            // Precompile / empty code: emit a minimal frame to fill the slot.
            WriteEmptyFrame(frame.Code);
        }

        bool hadPendingParentOp = frame.HasPendingParentOpToClose;
        ReturnFrame(frame);

        if (hadPendingParentOp)
        {
            _writer.WriteEndObject();
            _currentOperation = null;
        }

        _gasAlreadySetForCurrentOp = false;
        if (_actionStack.Peek().Type != "suicide")
        {
            _treatGasParityStyle = true;
        }
    }

    public override void StartOperation(int pc, Instruction opcode, long gas, in ExecutionEnvironment env)
    {
        if (!_streamVmTrace)
        {
            base.StartOperation(pc, opcode, gas, env);
            return;
        }

        StreamingVmFrame frame = _streamingFrames.Peek();
        if (!frame.JsonObjectOpened)
        {
            OpenFrameJson(frame);
        }

        // Emit the previous op with an implicit null sub (a sub would have been emitted
        // by PushVmTraceFrame, clearing _currentOperation).
        if (_currentOperation is not null)
        {
            WriteOperationOpening(_currentOperation);
            _writer.WriteNullValue();
            _writer.WriteEndObject();
            ReleaseOpBuffers();
        }

        // Reuse a single op buffer per tracer — only one op is alive at a time in streaming mode.
        ParityVmOperationTrace op = _opBuffer ??= new ParityVmOperationTrace();
        op.Pc = pc;
        op.Cost = gas;
        op.Used = 0;
        op.Memory = null;
        op.Push = null;
        op.Store = null;
        op.Sub = null;

        _gasAlreadySetForCurrentOp = false;
        _currentOperation = op;
    }

    public override void ReportOperationRemainingGas(long gas)
    {
        if (!_streamVmTrace)
        {
            base.ReportOperationRemainingGas(gas);
            return;
        }

        if (_gasAlreadySetForCurrentOp) return;
        _gasAlreadySetForCurrentOp = true;

        ParityVmOperationTrace op = _currentOperation!;
        op.Cost -= _treatGasParityStyle ? 0 : gas;
        // Parity quirk: stipend folded into reported cost.
        if (op.Cost == 7400) op.Cost = 9700;
        op.Used = gas;
        // Pushes live in _streamingPushList; skip the base's op.Push = ToArray().
        _treatGasParityStyle = false;
    }

    public override void ReportStackPush(in ReadOnlySpan<byte> stackItem)
    {
        if (!_streamVmTrace)
        {
            base.ReportStackPush(stackItem);
            return;
        }

        _streamingPushList.Add(PooledByteBuffer.Rent(stackItem));
    }

    public override void ReportMemoryChange(long offset, in ReadOnlySpan<byte> data)
    {
        if (!_streamVmTrace)
        {
            base.ReportMemoryChange(offset, data);
            return;
        }

        if (data.Length == 0) return;

        _memoryData.Dispose();
        _memoryData = PooledByteBuffer.Rent(data);
        _memoryOffset = offset;
    }

    public override void ReportStorageChange(in ReadOnlySpan<byte> key, in ReadOnlySpan<byte> value)
    {
        if (!_streamVmTrace)
        {
            base.ReportStorageChange(key, value);
            return;
        }

        _storageKey.Dispose();
        _storageValue.Dispose();
        _storageKey = PooledByteBuffer.Rent(key);
        _storageValue = PooledByteBuffer.Rent(value);
    }

    private void ReleaseOpBuffers()
    {
        _memoryData.Dispose();
        _memoryOffset = 0;
        _storageKey.Dispose();
        _storageValue.Dispose();

        for (int i = 0; i < _streamingPushList.Count; i++)
        {
            _streamingPushList[i].Dispose();
        }
        _streamingPushList.Clear();
    }

    protected override void OnOperationRemoved(ParityVmOperationTrace? operationTrace)
    {
        if (!_streamVmTrace)
        {
            base.OnOperationRemoved(operationTrace);
            return;
        }

        // Discard the in-flight op and its pooled buffers — no later StartOperation
        // will run to release them.
        if (ReferenceEquals(_currentOperation, operationTrace))
        {
            _currentOperation = null;
            ReleaseOpBuffers();
        }
    }

    public override void ReportByteCode(ReadOnlyMemory<byte> byteCode)
    {
        if (!_streamVmTrace)
        {
            base.ReportByteCode(byteCode);
            return;
        }

        _streamingFrames.Peek().Code = byteCode.ToArray();
    }

    private void OpenFrameJson(StreamingVmFrame frame)
    {
        _writer.WriteStartObject();
        _writer.WritePropertyName("code"u8);
        JsonSerializer.Serialize(_writer, frame.Code ?? [], _jsonOptions);
        _writer.WritePropertyName("ops"u8);
        _writer.WriteStartArray();
        frame.JsonObjectOpened = true;
    }

    private void WriteEmptyFrame(byte[]? code)
    {
        _writer.WriteStartObject();
        _writer.WritePropertyName("code"u8);
        JsonSerializer.Serialize(_writer, code ?? [], _jsonOptions);
        _writer.WritePropertyName("ops"u8);
        _writer.WriteStartArray();
        _writer.WriteEndArray();
        _writer.WriteEndObject();
    }

    /// <summary>
    /// Writes everything up to and including the <c>"sub":</c> property name. Caller writes
    /// the sub value and the closing <c>}</c>. Layout matches <see cref="ParityVmOperationTraceConverter"/>.
    /// </summary>
    private void WriteOperationOpening(ParityVmOperationTrace op)
    {
        _writer.WriteStartObject();
        _writer.WriteNumber("cost"u8, op.Cost);
        _writer.WritePropertyName("ex"u8);
        _writer.WriteStartObject();

        _writer.WritePropertyName("mem"u8);
        if (_memoryData.HasValue)
        {
            _writer.WriteStartObject();
            _writer.WritePropertyName("data"u8);
            ByteArrayConverter.Convert(_writer, _memoryData.Span, skipLeadingZeros: false);
            _writer.WritePropertyName("off"u8);
            JsonSerializer.Serialize(_writer, _memoryOffset, _jsonOptions);
            _writer.WriteEndObject();
        }
        else
        {
            _writer.WriteNullValue();
        }

        _writer.WritePropertyName("push"u8);
        _writer.WriteStartArray();
        for (int i = 0; i < _streamingPushList.Count; i++)
        {
            ByteArrayConverter.Convert(_writer, _streamingPushList[i].Span, skipLeadingZeros: false);
        }
        _writer.WriteEndArray();

        _writer.WritePropertyName("store"u8);
        if (_storageKey.HasValue)
        {
            _writer.WriteStartObject();
            _writer.WritePropertyName("key"u8);
            ByteArrayConverter.Convert(_writer, _storageKey.Span, skipLeadingZeros: false);
            _writer.WritePropertyName("val"u8);
            ByteArrayConverter.Convert(_writer, _storageValue.Span, skipLeadingZeros: false);
            _writer.WriteEndObject();
        }
        else
        {
            _writer.WriteNullValue();
        }

        _writer.WriteNumber("used"u8, op.Used);
        _writer.WriteEndObject();

        _writer.WriteNumber("pc"u8, op.Pc);
        _writer.WritePropertyName("sub"u8);
    }
}
