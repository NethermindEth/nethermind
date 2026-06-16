// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Text.Json;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Nethermind.Blockchain.Tracing.ParityStyle;

/// <summary>
/// Streams the <c>vmTrace</c> portion of a parity-style trace straight to a
/// <see cref="Utf8JsonWriter"/> as opcodes execute. Action tree and state-diff stay
/// buffered in the base class — their JSON shape needs parent-known fields before children.
/// </summary>
/// <remarks>
/// The streamed value fills a slot the caller wrote as <c>"vmTrace":</c> before
/// constructing this tracer. When vmTrace is not requested but the slot is open, the
/// constructor writes <c>null</c>. Mirrors <see cref="GethStyle.GethLikeTxDirectStreamingTracer"/>.
/// </remarks>
public class StreamingParityLikeTxTracer : ParityLikeTxTracer
{
    private const int DefaultFlushIntervalEntries = 8192;
    private const int InitialFrameStackCapacity = 8;
    private const long ParityCallCostBeforeStipend = 7400;
    private const long ParityCallCostAfterStipend = 9700;

    private readonly Utf8JsonWriter _writer;
    private readonly PipeWriter? _pipeWriter;
    private readonly CancellationToken _cancellationToken;
    private readonly bool _fillVmTraceSlot;
    private readonly bool _streamVmTrace;
    private readonly int _flushIntervalEntries;

    private bool _hasPendingOp;
    private bool _pushAssigned;
    private int _pendingPc;
    private long _pendingCost;
    private long _pendingUsed;

    private byte[]? _memoryBuffer;
    private int _memoryByteCount;
    private long _memoryOffset;
    private bool _hasMemory;

    private byte[]? _storageKeyBuffer;
    private int _storageKeyByteCount;
    private byte[]? _storageValueBuffer;
    private int _storageValueByteCount;
    private bool _hasStorage;

    private readonly ArrayPoolList<(byte[] Buffer, int Length)> _pushItems = new(4);

    private readonly ArrayPoolList<VmFrame> _streamingFrames = new(InitialFrameStackCapacity);
    private readonly ArrayPoolList<VmFrame> _framePool = new(InitialFrameStackCapacity);

    private readonly ArrayPoolList<ParityTraceAction> _actionPool = new(InitialFrameStackCapacity);
    private readonly ArrayPoolList<ParityAccountStateChange> _accountStateChangePool = new(InitialFrameStackCapacity);
    private readonly ArrayPoolList<Dictionary<UInt256, ParityStateChange<byte[]>>> _storageDictPool = new(InitialFrameStackCapacity);
    private readonly ArrayPoolList<ParityStateChange<byte[]>> _byteStateChangePool = new(InitialFrameStackCapacity);
    private readonly ArrayPoolList<ParityStateChange<UInt256?>> _uint256StateChangePool = new(InitialFrameStackCapacity);

    private int _entriesSinceLastFlush;
    private bool _disposed;

    private bool _outerOpHasSubWritten;

    public StreamingParityLikeTxTracer(
        Block block,
        Transaction? tx,
        ParityTraceTypes parityTraceTypes,
        Utf8JsonWriter writer,
        PipeWriter? pipeWriter,
        CancellationToken cancellationToken,
        bool fillVmTraceSlot,
        int flushIntervalEntries = DefaultFlushIntervalEntries)
        : base(block, tx, parityTraceTypes)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (flushIntervalEntries <= 0) throw new ArgumentOutOfRangeException(nameof(flushIntervalEntries));

        _writer = writer;
        _pipeWriter = pipeWriter;
        _cancellationToken = cancellationToken;
        _fillVmTraceSlot = fillVmTraceSlot;
        _streamVmTrace = fillVmTraceSlot && IsTracingInstructions;
        _flushIntervalEntries = flushIntervalEntries;

        if (fillVmTraceSlot && !IsTracingInstructions)
        {
            _writer.WriteNullValue();
        }
    }

    public void ResetForNextTx(Block block, Transaction? tx)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_trace.Action is not null) ReturnActionTree(_trace.Action);
        if (_trace.StateChanges is not null) ReturnStateChanges(_trace.StateChanges);

        ResetTracerState(block, tx);

        _hasPendingOp = false;
        _pushAssigned = false;
        _pendingPc = 0;
        _pendingCost = 0;
        _pendingUsed = 0;
        _outerOpHasSubWritten = false;
        ReleaseOpBuffers();

        while (_streamingFrames.Count > 0)
        {
            ReturnFrame(PopLast(_streamingFrames));
        }

        _entriesSinceLastFlush = 0;

        if (_fillVmTraceSlot && !IsTracingInstructions)
        {
            _writer.WriteNullValue();
        }
    }

    public void ReleaseResources()
    {
        if (_disposed) return;
        _disposed = true;

        if (_trace.Action is not null)
        {
            ReturnActionTree(_trace.Action);
            _trace.Action = null;
        }
        if (_trace.StateChanges is not null)
        {
            ReturnStateChanges(_trace.StateChanges);
        }

        ReturnByteBuffer(ref _memoryBuffer);
        ReturnByteBuffer(ref _storageKeyBuffer);
        ReturnByteBuffer(ref _storageValueBuffer);
        ReleasePushBuffers();

        while (_streamingFrames.Count > 0)
        {
            ReturnFrame(PopLast(_streamingFrames));
        }

        while (_framePool.Count > 0)
        {
            PopLast(_framePool).ReleaseBuffers();
        }
        _streamingFrames.Dispose();
        _framePool.Dispose();
        _actionPool.Dispose();
        _accountStateChangePool.Dispose();
        _storageDictPool.Dispose();
        _byteStateChangePool.Dispose();
        _uint256StateChangePool.Dispose();
        _pushItems.Dispose();
    }

    private static T PopLast<T>(ArrayPoolList<T> list)
    {
        int last = list.Count - 1;
        T item = list[last];
        list.Truncate(last);
        return item;
    }

    private static T PeekLast<T>(ArrayPoolList<T> list) => list[list.Count - 1];

    protected override ParityTraceAction RentAction()
    {
        if (_actionPool.Count == 0) return new ParityTraceAction();
        ParityTraceAction action = PopLast(_actionPool);
        action.Reset();
        return action;
    }

    protected override CappedArray<int> RentTraceAddress(int length)
    {
        if (length == 0) return CappedArray<int>.Empty;
        return new CappedArray<int>(ArrayPool<int>.Shared.Rent(length), length);
    }

    private static void ReturnTraceAddress(CappedArray<int> address)
    {
        if (address.IsNotNull && address.UnderlyingLength > 0)
        {
            ArrayPool<int>.Shared.Return(address.UnderlyingArray!);
        }
    }

    protected override ParityAccountStateChange RentAccountStateChange()
    {
        if (_accountStateChangePool.Count == 0) return new ParityAccountStateChange();
        return PopLast(_accountStateChangePool);
    }

    protected override Dictionary<UInt256, ParityStateChange<byte[]>> RentStorageDictionary()
    {
        if (_storageDictPool.Count == 0) return [];
        return PopLast(_storageDictPool);
    }

    protected override ParityStateChange<byte[]> RentByteStateChange(byte[] before, byte[] after)
    {
        if (_byteStateChangePool.Count == 0) return new ParityStateChange<byte[]>(before, after);
        ParityStateChange<byte[]> sc = PopLast(_byteStateChangePool);
        sc.Before = before;
        sc.After = after;
        return sc;
    }

    protected override ParityStateChange<UInt256?> RentNullableUInt256StateChange(UInt256? before, UInt256? after)
    {
        if (_uint256StateChangePool.Count == 0) return new ParityStateChange<UInt256?>(before, after);
        ParityStateChange<UInt256?> sc = PopLast(_uint256StateChangePool);
        sc.Before = before;
        sc.After = after;
        return sc;
    }

    protected override CappedArray<byte> CopyInput(ReadOnlyMemory<byte> input)
    {
        if (input.IsEmpty) return CappedArray<byte>.Empty;
        byte[] rented = ArrayPool<byte>.Shared.Rent(input.Length);
        input.Span.CopyTo(rented);
        return new CappedArray<byte>(rented, input.Length);
    }

    protected override void ReturnInputBytes(in CappedArray<byte> input)
    {
        if (input.IsNotNull && input.UnderlyingLength > 0)
        {
            ArrayPool<byte>.Shared.Return(input.UnderlyingArray!);
        }
    }

    private void ReturnActionTree(ParityTraceAction action)
    {
        List<ParityTraceAction> subtraces = action.Subtraces;
        for (int i = 0; i < subtraces.Count; i++)
        {
            ReturnActionTree(subtraces[i]);
        }
        ReturnTraceAddress(action.TraceAddress);
        ReturnInputBytes(action.Input);
        action.Reset();
        _actionPool.Add(action);
    }

    private void ReturnStateChanges(Dictionary<Address, ParityAccountStateChange> changes)
    {
        foreach (ParityAccountStateChange account in changes.Values)
        {
            if (account.Storage is not null)
            {
                foreach (KeyValuePair<UInt256, ParityStateChange<byte[]>> kv in account.Storage)
                {
                    _byteStateChangePool.Add(kv.Value);
                }
                account.Storage.Clear();
                _storageDictPool.Add((Dictionary<UInt256, ParityStateChange<byte[]>>)account.Storage);
                account.Storage = null;
            }
            if (account.Balance is not null) { _uint256StateChangePool.Add(account.Balance); account.Balance = null; }
            if (account.Code is not null) { _byteStateChangePool.Add(account.Code); account.Code = null; }
            if (account.Nonce is not null) { _uint256StateChangePool.Add(account.Nonce); account.Nonce = null; }
            _accountStateChangePool.Add(account);
        }
    }

    public override void StartOperation(int pc, Instruction opcode, long gas, in ExecutionEnvironment env)
    {
        if (!_streamVmTrace) { base.StartOperation(pc, opcode, gas, env); return; }

        FinalizePendingOp(closeWithNullSub: true);

        VmFrame frame = PeekLast(_streamingFrames);
        if (!frame.JsonObjectOpened)
        {
            OpenFrameJson(frame);
        }

        _hasPendingOp = true;
        _pushAssigned = false;
        _pendingPc = pc;
        _pendingCost = gas;
        _pendingUsed = 0;
        _gasAlreadySetForCurrentOp = false;
        ReleaseOpBuffers();
    }

    public override void ReportOperationRemainingGas(long gas)
    {
        if (!_streamVmTrace) { base.ReportOperationRemainingGas(gas); return; }

        if (_gasAlreadySetForCurrentOp || !_hasPendingOp) return;
        _gasAlreadySetForCurrentOp = true;

        _pendingCost -= _treatGasParityStyle ? 0 : gas;
        if (_pendingCost == ParityCallCostBeforeStipend) _pendingCost = ParityCallCostAfterStipend;
        _pendingUsed = gas;
        _pushAssigned = true;
        _treatGasParityStyle = false;
    }

    public override void ReportOperationError(EvmExceptionType error)
    {
        if (!_streamVmTrace) { base.ReportOperationError(error); return; }

        if (error != EvmExceptionType.InvalidJumpDestination && error != EvmExceptionType.NotEnoughBalance)
        {
            _hasPendingOp = false;
            ReleaseOpBuffers();
        }
    }

    public override void ReportStackPush(in ReadOnlySpan<byte> stackItem)
    {
        if (!_streamVmTrace) { base.ReportStackPush(stackItem); return; }

        if (stackItem.IsEmpty)
        {
            _pushItems.Add((Array.Empty<byte>(), 0));
            return;
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(stackItem.Length);
        stackItem.CopyTo(buffer);
        _pushItems.Add((buffer, stackItem.Length));
    }

    public override void ReportMemoryChange(long offset, in ReadOnlySpan<byte> data)
    {
        if (!_streamVmTrace) { base.ReportMemoryChange(offset, data); return; }

        if (data.IsEmpty) return;

        EnsureBuffer(ref _memoryBuffer, data.Length);
        data.CopyTo(_memoryBuffer);
        _memoryByteCount = data.Length;
        _memoryOffset = offset;
        _hasMemory = true;
    }

    public override void ReportStorageChange(in ReadOnlySpan<byte> key, in ReadOnlySpan<byte> value)
    {
        if (!_streamVmTrace) { base.ReportStorageChange(key, value); return; }

        EnsureBuffer(ref _storageKeyBuffer, key.Length);
        key.CopyTo(_storageKeyBuffer);
        _storageKeyByteCount = key.Length;

        EnsureBuffer(ref _storageValueBuffer, value.Length);
        value.CopyTo(_storageValueBuffer);
        _storageValueByteCount = value.Length;

        _hasStorage = true;
    }

    public override void ReportByteCode(ReadOnlyMemory<byte> byteCode)
    {
        if (!_streamVmTrace) { base.ReportByteCode(byteCode); return; }

        if (_streamingFrames.Count == 0) return;

        VmFrame frame = PeekLast(_streamingFrames);
        ReadOnlySpan<byte> codeSpan = byteCode.Span;
        EnsureBuffer(ref frame.CodeBuffer, codeSpan.Length);
        codeSpan.CopyTo(frame.CodeBuffer);
        frame.CodeLength = codeSpan.Length;
    }

    public override void ReportGasUpdateForVmTrace(long refund, long gasAvailable)
    {
        if (!_streamVmTrace) { base.ReportGasUpdateForVmTrace(refund, gasAvailable); return; }

        if (_hasPendingOp) _pendingUsed = gasAvailable;
    }

    protected override void OnEnterVmFrame(ParityTraceAction action)
    {
        if (!_streamVmTrace) { base.OnEnterVmFrame(action); return; }

        VmFrame frame = RentFrame();

        if (action.Type == "suicide")
        {
            frame.IsSuicide = true;
            _streamingFrames.Add(frame);
            return;
        }

        if (_hasPendingOp)
        {
            _writer.WriteStartObject();
            _writer.WritePropertyName("sub"u8);
            frame.OuterPendingPc = _pendingPc;
            frame.OuterPendingCost = _pendingCost;
            _hasPendingOp = false;
            frame.HasPendingParentOpToClose = true;
        }

        _streamingFrames.Add(frame);
    }

    protected override void OnLeaveVmFrame(ParityTraceAction action)
    {
        if (!_streamVmTrace) { base.OnLeaveVmFrame(action); return; }

        VmFrame frame = PopLast(_streamingFrames);

        if (frame.IsSuicide)
        {
            ReturnFrame(frame);
            return;
        }

        FinalizePendingOp(closeWithNullSub: true);

        if (frame.JsonObjectOpened)
        {
            _writer.WriteEndArray();
            _writer.WriteEndObject();
        }
        else
        {
            WriteEmptyFrame(frame);
        }

        bool hadPendingParent = frame.HasPendingParentOpToClose;
        int outerPc = frame.OuterPendingPc;
        long outerCost = frame.OuterPendingCost;
        ReturnFrame(frame);

        if (hadPendingParent)
        {
            ReleaseOpBuffers();
            _pendingPc = outerPc;
            _pendingCost = outerCost;
            _pendingUsed = 0;
            _pushAssigned = false;
            _gasAlreadySetForCurrentOp = false;
            _hasPendingOp = true;
            _outerOpHasSubWritten = true;
        }

        _gasAlreadySetForCurrentOp = false;
        _treatGasParityStyle = true;

        MaybeFlushToWire();
    }

    public override ParityLikeTxTrace BuildResult()
    {
        if (_streamVmTrace)
        {
            FinalizePendingOp(closeWithNullSub: true);
        }
        return base.BuildResult();
    }

    private void FinalizePendingOp(bool closeWithNullSub)
    {
        if (!_hasPendingOp) return;

        if (_outerOpHasSubWritten)
        {
            EmitOuterOpTail();
            _outerOpHasSubWritten = false;
        }
        else
        {
            EmitPendingOpUpToSub();
            if (closeWithNullSub)
            {
                _writer.WriteNullValue();
                _writer.WriteEndObject();
            }
        }

        _hasPendingOp = false;
        ReleaseOpBuffers();
        _entriesSinceLastFlush++;
        MaybeFlushToWire();
    }

    private void EmitOuterOpTail()
    {
        _writer.WriteNumber("cost"u8, _pendingCost);

        _writer.WritePropertyName("ex"u8);
        _writer.WriteStartObject();

        _writer.WritePropertyName("mem"u8);
        if (_hasMemory)
        {
            _writer.WriteStartObject();
            _writer.WritePropertyName("data"u8);
            WriteHexBytes(_memoryBuffer.AsSpan(0, _memoryByteCount));
            _writer.WriteNumber("off"u8, _memoryOffset);
            _writer.WriteEndObject();
        }
        else
        {
            _writer.WriteNullValue();
        }

        _writer.WritePropertyName("push"u8);
        if (_pushAssigned)
        {
            _writer.WriteStartArray();
            for (int i = 0; i < _pushItems.Count; i++)
            {
                (byte[] buf, int len) = _pushItems[i];
                WriteHexBytes(buf.AsSpan(0, len));
            }
            _writer.WriteEndArray();
        }
        else
        {
            _writer.WriteNullValue();
        }

        _writer.WritePropertyName("store"u8);
        if (_hasStorage)
        {
            _writer.WriteStartObject();
            _writer.WritePropertyName("key"u8);
            WriteHexBytes(_storageKeyBuffer.AsSpan(0, _storageKeyByteCount));
            _writer.WritePropertyName("val"u8);
            WriteHexBytes(_storageValueBuffer.AsSpan(0, _storageValueByteCount));
            _writer.WriteEndObject();
        }
        else
        {
            _writer.WriteNullValue();
        }

        _writer.WriteNumber("used"u8, _pendingUsed);
        _writer.WriteEndObject();

        _writer.WriteNumber("pc"u8, _pendingPc);
        _writer.WriteEndObject();
    }

    private void EmitPendingOpUpToSub()
    {
        _writer.WriteStartObject();
        _writer.WriteNumber("cost"u8, _pendingCost);

        _writer.WritePropertyName("ex"u8);
        _writer.WriteStartObject();

        _writer.WritePropertyName("mem"u8);
        if (_hasMemory)
        {
            _writer.WriteStartObject();
            _writer.WritePropertyName("data"u8);
            WriteHexBytes(_memoryBuffer.AsSpan(0, _memoryByteCount));
            _writer.WriteNumber("off"u8, _memoryOffset);
            _writer.WriteEndObject();
        }
        else
        {
            _writer.WriteNullValue();
        }

        _writer.WritePropertyName("push"u8);
        if (_pushAssigned)
        {
            _writer.WriteStartArray();
            for (int i = 0; i < _pushItems.Count; i++)
            {
                (byte[] buf, int len) = _pushItems[i];
                WriteHexBytes(buf.AsSpan(0, len));
            }
            _writer.WriteEndArray();
        }
        else
        {
            _writer.WriteNullValue();
        }

        _writer.WritePropertyName("store"u8);
        if (_hasStorage)
        {
            _writer.WriteStartObject();
            _writer.WritePropertyName("key"u8);
            WriteHexBytes(_storageKeyBuffer.AsSpan(0, _storageKeyByteCount));
            _writer.WritePropertyName("val"u8);
            WriteHexBytes(_storageValueBuffer.AsSpan(0, _storageValueByteCount));
            _writer.WriteEndObject();
        }
        else
        {
            _writer.WriteNullValue();
        }

        _writer.WriteNumber("used"u8, _pendingUsed);
        _writer.WriteEndObject();

        _writer.WriteNumber("pc"u8, _pendingPc);
        _writer.WritePropertyName("sub"u8);
    }

    private void OpenFrameJson(VmFrame frame)
    {
        _writer.WriteStartObject();
        _writer.WritePropertyName("code"u8);
        WriteHexBytes(frame.CodeBuffer.AsSpan(0, frame.CodeLength));
        _writer.WritePropertyName("ops"u8);
        _writer.WriteStartArray();
        frame.JsonObjectOpened = true;
    }

    private void WriteEmptyFrame(VmFrame frame)
    {
        _writer.WriteStartObject();
        _writer.WritePropertyName("code"u8);
        WriteHexBytes(frame.CodeBuffer.AsSpan(0, frame.CodeLength));
        _writer.WritePropertyName("ops"u8);
        _writer.WriteStartArray();
        _writer.WriteEndArray();
        _writer.WriteEndObject();
    }

    private void WriteHexBytes(ReadOnlySpan<byte> bytes) =>
        ByteArrayConverter.Convert(_writer, bytes, skipLeadingZeros: false);

    private void MaybeFlushToWire()
    {
        if (_pipeWriter is null || _entriesSinceLastFlush < _flushIntervalEntries) return;
        _writer.Flush();
        _pipeWriter.FlushAsync(_cancellationToken).SafeWait();
        _entriesSinceLastFlush = 0;
    }

    private void ReleaseOpBuffers()
    {
        _hasMemory = false;
        _memoryByteCount = 0;
        _hasStorage = false;
        _storageKeyByteCount = 0;
        _storageValueByteCount = 0;
        ReleasePushBuffers();
    }

    private void ReleasePushBuffers()
    {
        Span<(byte[] Buffer, int Length)> span = _pushItems.AsSpan();
        for (int i = 0; i < span.Length; i++)
        {
            byte[] buffer = span[i].Buffer;
            if (buffer.Length > 0) ArrayPool<byte>.Shared.Return(buffer);
        }
        _pushItems.Clear();
    }

    private static void EnsureBuffer(ref byte[]? buffer, int requiredLength)
    {
        if (buffer is not null && buffer.Length >= requiredLength) return;
        if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);
        buffer = ArrayPool<byte>.Shared.Rent(requiredLength);
    }

    private static void ReturnByteBuffer(ref byte[]? buffer)
    {
        if (buffer is null) return;
        ArrayPool<byte>.Shared.Return(buffer);
        buffer = null;
    }

    private VmFrame RentFrame() => _framePool.Count > 0 ? PopLast(_framePool) : new VmFrame();

    private void ReturnFrame(VmFrame frame)
    {
        frame.Reset();
        _framePool.Add(frame);
    }

    private sealed class VmFrame
    {
        public byte[]? CodeBuffer;
        public int CodeLength;
        public bool IsSuicide;
        public bool JsonObjectOpened;
        public bool HasPendingParentOpToClose;
        public int OuterPendingPc;
        public long OuterPendingCost;

        public void Reset()
        {
            ReleaseBuffers();
            IsSuicide = false;
            JsonObjectOpened = false;
            HasPendingParentOpToClose = false;
            OuterPendingPc = 0;
            OuterPendingCost = 0;
        }

        public void ReleaseBuffers()
        {
            if (CodeBuffer is not null)
            {
                ArrayPool<byte>.Shared.Return(CodeBuffer);
                CodeBuffer = null;
            }
            CodeLength = 0;
        }
    }
}
