// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Nethermind.Blockchain.Tracing.ParityStyle;

/// <summary>
/// A <see cref="ParityLikeTxTracer"/> that streams the <c>vmTrace</c> portion of the
/// trace straight to a <see cref="Utf8JsonWriter"/> as opcodes execute, instead of
/// accumulating <see cref="ParityVmOperationTrace"/> entries in memory. Peak heap for
/// the vmTrace tree drops from O(opcodes) to O(call-depth) regardless of trace length.
/// </summary>
/// <remarks>
/// The non-vmTrace parts of the trace (action tree, state-diff, output) are still
/// buffered in memory: their JSON shape requires parent-known fields (<c>subtraces</c>
/// count, pre-order child enumeration) before children are knowable, so they can't be
/// emitted incrementally. They're bounded by call-depth × subtraces, typically tens
/// of KB even for complex transactions.
/// <para>
/// The streamed <c>vmTrace</c> value is written into a pre-positioned slot: the caller
/// is responsible for writing the property name <c>"vmTrace":</c> immediately before
/// constructing this tracer. If <c>vmTrace</c> wasn't requested, the constructor writes
/// <c>null</c> at that position and behaves as the base tracer for everything else.
/// </para>
/// </remarks>
public class StreamingParityLikeTxTracer : ParityLikeTxTracer
{
    private readonly Utf8JsonWriter _writer;
    private readonly bool _streamVmTrace;
    private readonly JsonSerializerOptions _jsonOptions;

    // One streaming frame per active call-frame; mirrors _vmTraceStack but holds
    // only what's needed for streaming (frame JSON state, pending parent-op flag).
    private readonly Stack<StreamingVmFrame> _streamingFrames = new();

    // Pool of reusable StreamingVmFrame instances. Push/pop rents/returns; growth is
    // bounded by max call depth (rarely > 1024), keeping per-frame allocations off the
    // GC heap after the first few transactions.
    private readonly Stack<StreamingVmFrame> _framePool = new();

    // Single reusable ParityVmOperationTrace buffer in streaming mode: only one op is
    // "live" at a time (the one we're building up before emitting), so we mutate this
    // instance in place instead of allocating per opcode. For a million-opcode trace
    // this is the biggest GC pressure we can eliminate.
    private ParityVmOperationTrace? _opBuffer;

    // Per-opcode payload state, all backed by ArrayPool-rented buffers via PooledByteBuffer.
    // These live only until the current op is emitted, then Dispose() returns the underlying
    // arrays to the pool. The base tracer would allocate fresh wrappers + byte[]s for every
    // memory/storage/stack-push report; we avoid that entirely.
    private PooledByteBuffer _memoryData;
    private long _memoryOffset;

    private PooledByteBuffer _storageKey;
    private PooledByteBuffer _storageValue;

    // The streaming push list mirrors the base tracer's <c>_currentPushList</c> but stores
    // pooled buffers so we never allocate per-push byte[]s.
    private readonly List<PooledByteBuffer> _streamingPushList = [];

    // Pool of ParityTraceAction instances reused across every call frame in every
    // transaction in this block. Each pooled action keeps its non-null Result and its
    // Subtraces list backing array, so a tx with thousands of nested calls allocates only
    // as many actions as the deepest historical breadth ever required.
    private readonly Stack<ParityTraceAction> _actionPool = new();

    // Pools for the per-account state-change graph. The top-level Dictionary in
    // <c>_trace.StateChanges</c> is already reused via <see cref="ParityLikeTxTracer.ResetTracerState"/>,
    // but the per-account ParityAccountStateChange instances, their Storage dictionaries,
    // and the per-cell ParityStateChange wrappers used to churn through GC every tx.
    // These pools let a long-running node accumulate a steady-state working set sized to
    // the worst-touch tx and then make zero further state-diff allocations.
    private readonly Stack<ParityAccountStateChange> _accountStateChangePool = new();
    private readonly Stack<Dictionary<UInt256, ParityStateChange<byte[]>>> _storageDictPool = new();

    /// <summary>
    /// Creates a streaming tx tracer.
    /// </summary>
    /// <param name="fillVmTraceSlot">
    /// Set to <see langword="true"/> when the caller has just written <c>"vmTrace":</c> on
    /// <paramref name="writer"/> and expects this tracer to populate that JSON slot
    /// (either by streaming the value per-opcode or by emitting <c>null</c>). Set to
    /// <see langword="false"/> when no vmTrace slot is open (e.g. Store-mode block tracer
    /// where vmTrace isn't part of the response) — in that case the tracer behaves like
    /// the base buffered tracer and writes nothing.
    /// </param>
    /// <param name="streamActionsInline">
    /// When <see langword="true"/>, the tracer emits each action's JSON (in the
    /// <c>ParityTxTraceFromStore</c> shape) directly to <paramref name="writer"/> at the
    /// action's <see cref="PopAction"/>, in post-order traversal, instead of building a
    /// tree in memory for the caller to walk later. Saves the per-tx action-tree buffer
    /// (working set drops from O(action count) to O(call depth)). Only valid when no
    /// other field is competing for the writer during execution — i.e. <paramref name="fillVmTraceSlot"/>
    /// is <see langword="false"/> (Store mode). Replay mode must keep the action tree
    /// buffered because vmTrace owns the writer during execution.
    /// </param>
    /// <param name="actionFilter">
    /// Optional predicate applied to each action at emit time when
    /// <paramref name="streamActionsInline"/> is <see langword="true"/>. Returning
    /// <see langword="false"/> drops the item from the output stream; lets
    /// <c>trace_filter</c> apply its address / after / count filter without buffering.
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
            // Caller opened the "vmTrace":<here> slot but no vmTrace data will be produced.
            _writer.WriteNullValue();
        }
    }

    private readonly bool _fillVmTraceSlot;
    private readonly bool _streamActionsInline;
    private readonly Func<ParityTraceAction, bool>? _actionFilter;

    /// <summary>Read-only accessor on the base's protected trace-types field so the block tracer can decide whether the cached instance is compatible with a new tx.</summary>
    public ParityTraceTypes ParityTraceTypes => _parityTraceTypes;

    /// <summary>
    /// Re-init this tracer for the next transaction in the same block. Keeps every
    /// pooled allocation (action stack, vm-trace stack, push list, streaming frame
    /// pool, per-opcode buffer) alive across transactions — the block tracer holds
    /// a single instance and just calls this between txs.
    /// </summary>
    public void ResetForNextTx(Block block, Transaction? tx)
    {
        // Return the previous tx's action tree to the pool before ResetTracerState drops
        // the root reference. Children are returned depth-first; the per-action Reset()
        // runs on the next RentAction().
        if (_trace?.Action is not null)
        {
            ReturnActionTree(_trace.Action);
        }

        // Same for the state-change graph: return per-account changes, storage maps, and
        // state-change wrappers to their pools before the base clears the dictionary.
        if (_trace?.StateChanges is not null)
        {
            ReturnStateChanges(_trace.StateChanges);
        }

        ResetTracerState(block, tx);

        // The streaming-mode flag is derived from IsTracingInstructions which is set
        // in the base ctor from the trace types. Trace types don't change across txs
        // (same block tracer => same types), so _streamVmTrace stays correct.

        // Drain any leftover streaming frames defensively (shouldn't happen for a
        // well-formed run, but a partial execution from a cancelled tx might leave state).
        while (_streamingFrames.Count > 0)
        {
            ReturnFrame(_streamingFrames.Pop());
        }

        // Return any in-flight pooled buffers; same defensive concern as the frame stack.
        ReleaseOpBuffers();

        if (_fillVmTraceSlot && !IsTracingInstructions)
        {
            // Re-emit the null for the next tx's vmTrace slot.
            _writer.WriteNullValue();
        }
    }

    private sealed class StreamingVmFrame
    {
        /// <summary>Marker for SELFDESTRUCT frames: no JSON is emitted; <see cref="PopVmTraceFrame"/> just pops.</summary>
        public bool IsSuicide;
        /// <summary>Has <c>{"code":...,"ops":[</c> been written yet?</summary>
        public bool JsonObjectOpened;
        /// <summary>Bytecode of this frame; buffered until the object opens.</summary>
        public byte[]? Code;
        /// <summary>True iff this frame is the <c>"sub"</c> value of a parent op whose opening (without the closing <c>}</c>) was already written; the closing brace is written when this frame returns.</summary>
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

    /// <summary>
    /// Returns the per-account state-change instances and their storage dictionaries to
    /// their pools. The per-cell <see cref="ParityStateChange{T}"/> wrappers themselves
    /// are left for the GC — they're small (~32B each) and pooling them adds two more
    /// type-specific Stacks for a modest benefit. The top-level dictionary is reused
    /// in-place by the base <see cref="ParityLikeTxTracer.ResetTracerState"/>.
    /// </summary>
    private void ReturnStateChanges(Dictionary<Address, ParityAccountStateChange> changes)
    {
        foreach (ParityAccountStateChange account in changes.Values)
        {
            if (account.Storage is not null)
            {
                account.Storage.Clear();
                _storageDictPool.Push(account.Storage);
            }
            account.Storage = null;
            account.Balance = null;
            account.Code = null;
            account.Nonce = null;
            _accountStateChangePool.Push(account);
        }
    }

    /// <summary>
    /// Recursively returns an action and all of its subtraces to the pool. Called from
    /// <see cref="ResetForNextTx"/> on the root before the next tx overwrites it; the
    /// per-action <see cref="ParityTraceAction.Reset"/> happens lazily on the next
    /// <see cref="RentAction"/> call.
    /// </summary>
    private void ReturnActionTree(ParityTraceAction action)
    {
        List<ParityTraceAction> subtraces = action.Subtraces;
        for (int i = 0; i < subtraces.Count; i++)
        {
            ReturnActionTree(subtraces[i]);
        }
        subtraces.Clear();
        action.TraceAddress = null;
        _actionPool.Push(action);
    }

    /// <summary>
    /// In <c>streamActionsInline</c> mode we never use the subtraces list — actions emit
    /// themselves at <see cref="PopAction"/> and the parent's <see cref="ParityTraceAction.IncludedSubtraceCount"/>
    /// (maintained by base <c>PushAction</c>) carries everything <c>traceAddress</c>
    /// indexing and JSON output need. Skipping the <c>List.Add</c> keeps the tree out
    /// of memory entirely.
    /// </summary>
    protected override void RegisterSubtrace(ParityTraceAction parent, ParityTraceAction child)
    {
        if (_streamActionsInline) return;
        base.RegisterSubtrace(parent, child);
    }

    public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
    {
        if (_streamActionsInline)
        {
            // Root action was already emitted at its PopAction and returned to the pool;
            // the base behaviour would re-dereference _trace.Action.Result and write the
            // output onto a now-pooled instance. The output was already written by the
            // closing ReportActionEnd before the action emitted, so this is a no-op.
            // _trace.Output is part of the Replay envelope, not Store, so we skip it too.
            return;
        }
        base.MarkAsSuccess(recipient, gasSpent, output, logs, stateRoot);
    }

    public override void MarkAsFailed(Address recipient, in GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null)
    {
        if (_streamActionsInline)
        {
            // Quick-fail path: no PushAction ever fired (tx rejected before execution), so
            // _trace.Action is still null. Build a minimal failure action and emit it inline
            // so the user still sees the failed call. If PushAction did fire, the action
            // tree was already emitted; nothing more to do.
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
                action.TraceAddress = [];

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

    /// <summary>
    /// Post-order action emit: when an action pops, all of its children have already
    /// emitted (so its <see cref="ParityTraceAction.IncludedSubtraceCount"/> is final)
    /// and its <see cref="ParityTraceAction.Result"/> / <see cref="ParityTraceAction.Error"/>
    /// have just been set by the closing <c>ReportActionEnd</c> / <c>ReportActionError</c>.
    /// Emit the action's JSON now and return it to the pool — the action tree never
    /// materializes in memory.
    /// </summary>
    protected override void OnActionPopped(ParityTraceAction action)
    {
        if (!_streamActionsInline) return;

        if (action.IncludeInTrace && (_actionFilter is null || _actionFilter(action)))
        {
            WriteStoreActionJson(_writer, action, _trace, _jsonOptions);
        }

        action.TraceAddress = null;
        _actionPool.Push(action);
    }

    /// <summary>
    /// Writes a single action in the <c>ParityTxTraceFromStore</c> JSON shape: action
    /// fields, block/tx context, result-or-error, subtraces count, traceAddress, type.
    /// Matches the property order produced by the buffered <c>JsonSerializer.Serialize</c>
    /// path over <see cref="Nethermind.JsonRpc.Modules.Trace.ParityTxTraceFromStore"/>
    /// (declaration order under camelCase: action, blockHash, blockNumber, result|error,
    /// subtraces, traceAddress, transactionHash, transactionPosition, type).
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
        JsonSerializer.Serialize(writer, action.TraceAddress ?? [], options);

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
            // Base would create a frame but not link Sub on the parent op; output JSON omits
            // suicide sub-trees. Mirror that here: keep the stack symmetric with a marker
            // frame so PopVmTraceFrame matches up, but emit no JSON.
            frame.IsSuicide = true;
            _streamingFrames.Push(frame);
            return;
        }

        if (_currentOperation is not null)
        {
            // Parent's CALL/CREATE op triggered this sub-frame. Emit its prefix and open the
            // "sub" slot; the closing brace is written when this sub-frame returns. We don't
            // hold a reference to the op itself — the buffer is reused for sub-frame opcodes
            // and would be mutated out from under us; just remember that we owe a `}`.
            WriteOperationOpening(_currentOperation);
            frame.HasPendingParentOpToClose = true;
            // The pooled buffers attached to the parent op (memory / storage / push) have
            // now been written out; release them so sub-frame opcodes can reuse the slots.
            ReleaseOpBuffers();
        }
        // else: root frame — the caller has already written the "vmTrace":<here> slot.

        _streamingFrames.Push(frame);
        // Clear so the next StartOperation in this new frame doesn't try to emit the parent op.
        _currentOperation = null;
    }

    protected override void PopVmTraceFrame()
    {
        StreamingVmFrame frame = _streamingFrames.Pop();

        if (frame.IsSuicide)
        {
            // No JSON written; no _currentOperation change needed.
            ReturnFrame(frame);
            if (_actionStack.Peek().Type != "suicide")
            {
                _treatGasParityStyle = true;
            }
            return;
        }

        // Emit any op left buffered in this frame (its sub is implicitly null — if it had
        // a sub, PushVmTraceFrame would have consumed it and cleared _currentOperation).
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
            _writer.WriteEndArray();   // close "ops"
            _writer.WriteEndObject();  // close vmTrace frame object
        }
        else
        {
            // Frame had no opcodes (precompile / empty code). Emit the minimal frame so the
            // "sub":<here> or "vmTrace":<here> slot has a valid value.
            WriteEmptyFrame(frame.Code);
        }

        bool hadPendingParentOp = frame.HasPendingParentOpToClose;
        ReturnFrame(frame);

        if (hadPendingParentOp)
        {
            // Close the parent op whose opening was written when this sub frame started.
            _writer.WriteEndObject();
            // Parent op is now fully emitted; don't double-emit on next StartOperation in the parent frame.
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

        // Emit the previous op (no sub — if it had triggered a sub, PushVmTraceFrame would
        // have already emitted its opening and cleared _currentOperation). The previous op
        // owns the pooled buffers in _memoryDataBuf / _storageKeyBuf / _storageValueBuf /
        // _streamingPushList; WriteOperationOpening reads them, then ReleaseOpBuffers
        // returns them to ArrayPool.
        if (_currentOperation is not null)
        {
            WriteOperationOpening(_currentOperation);
            _writer.WriteNullValue();
            _writer.WriteEndObject();
            ReleaseOpBuffers();
        }

        // Reuse a single ParityVmOperationTrace buffer across every opcode in this tracer.
        // The base tracer's StartOperation would `new` one per opcode; for million-opcode
        // traces that's tens-of-MB of GC churn we don't need to pay because in streaming
        // mode only one op is alive at a time. Reset fields in place rather than allocate.
        ParityVmOperationTrace op = _opBuffer ??= new ParityVmOperationTrace();
        op.Pc = pc;
        op.Cost = gas;
        op.Used = 0;
        op.Memory = null;
        op.Push = null;
        op.Store = null;
        op.Sub = null;

        _gasAlreadySetForCurrentOp = false;
        // _currentPushList stays empty in streaming mode; we feed _streamingPushList instead.
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
        // Skip op.Push = _currentPushList.ToArray() — we already hold the pushes in
        // _streamingPushList with pooled buffers, and WriteOperationOpening reads from there.
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

        // Defensive — should have been released in StartOperation already.
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

        // Discard the buffered op AND its pooled buffers — those would otherwise leak until
        // the tracer is disposed, because no later StartOperation will run to release them.
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

        // Buffer the code; it's emitted when the frame's JSON object opens.
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
    /// Writes the opening of an operation's JSON object: everything up to and including the
    /// <c>"sub":</c> property name. Caller is responsible for writing the sub value (null or
    /// a streamed sub-frame) and the closing <c>}</c>. Mirrors the layout produced by
    /// <see cref="ParityVmOperationTraceConverter"/>.
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
        _writer.WriteEndObject(); // close "ex"

        _writer.WriteNumber("pc"u8, op.Pc);
        _writer.WritePropertyName("sub"u8);
        // Caller writes the sub value (null or streamed sub-frame) and the closing }.
    }
}
