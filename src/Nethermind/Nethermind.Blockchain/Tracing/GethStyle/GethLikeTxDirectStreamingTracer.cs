// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Text.Json;
using System.Threading;
using Collections.Pooled;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Nethermind.Blockchain.Tracing.GethStyle;

/// <summary>
/// Streams Geth-style struct-log entries directly to a <see cref="Utf8JsonWriter"/> without
/// allocating per-opcode <see cref="GethTxMemoryTraceEntry"/> objects or cloning the storage
/// dictionary on every opcode. Bounded peak memory regardless of trace length.
/// </summary>
public sealed class GethLikeTxDirectStreamingTracer : GethLikeTxTracer
{
    private const int DefaultFlushIntervalEntries = 8192;
    private const int EvmWordSize = 32;
    private static readonly JsonEncodedText ZeroMemoryWord = JsonEncodedText.Encode(new string('0', EvmWordSize * 2));
    private const int InitialStorageMapCapacity = 8;
    private const int InitialDepthStackCapacity = 8;

    private readonly Utf8JsonWriter _writer;
    private readonly PipeWriter? _pipeWriter;
    private readonly CancellationToken _cancellationToken;
    private Transaction? _transaction;
    private readonly int _flushIntervalEntries;

    private bool _hasPendingOpcode;
    private int _pendingPc;
    private Instruction _pendingOpcode;
    private long _pendingGas;
    private long _pendingGasCost;
    private int _pendingDepth;
    private string? _pendingError;
    private long _pendingRefund;
    private bool _gasCostAlreadySet;

    private long _refund;
    private readonly Stack<long> _refundCheckpoints = new();

    private byte[]? _stackBuffer;
    private int _stackByteCount;

    private byte[]? _memoryBuffer;
    private int _memoryByteCount;

    private byte[]? _returnDataBuffer;
    private int _returnDataByteCount;
    private byte[]? _returnDataHexBuffer;

    private ArrayPoolList<PooledDictionary<UInt256, UInt256>>? _storageByDepth;
    private int _activeStorageDepth;

    private int _entriesSinceLastFlush;
    private bool _disposed;

    public GethLikeTxDirectStreamingTracer(
        Transaction? transaction,
        GethTraceOptions options,
        Utf8JsonWriter writer,
        PipeWriter? pipeWriter,
        CancellationToken cancellationToken,
        int flushIntervalEntries = DefaultFlushIntervalEntries)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (flushIntervalEntries <= 0) throw new ArgumentOutOfRangeException(nameof(flushIntervalEntries));

        _transaction = transaction;
        _writer = writer;
        _pipeWriter = pipeWriter;
        _cancellationToken = cancellationToken;
        _flushIntervalEntries = flushIntervalEntries;
        IsTracingMemory = IsTracingFullMemory;
        IsTracingRefunds = true;
        IsTracingActions = true;
    }

    internal void ResetForNextTx(Transaction? transaction)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _transaction = transaction;
        _hasPendingOpcode = false;
        _pendingPc = 0;
        _pendingOpcode = default;
        _pendingGas = 0;
        _pendingGasCost = 0;
        _pendingDepth = 0;
        _pendingError = null;
        _pendingRefund = 0;
        _gasCostAlreadySet = false;
        _refund = 0;
        _refundCheckpoints.Clear();
        _stackByteCount = 0;
        _memoryByteCount = 0;
        _returnDataByteCount = 0;
        if (_storageByDepth is not null)
        {
            for (int i = 0; i < _activeStorageDepth; i++) _storageByDepth[i].Clear();
        }
        _activeStorageDepth = 0;
        _entriesSinceLastFlush = 0;
        ResetTrace();
    }

    public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
    {
        base.MarkAsSuccess(recipient, gasSpent, output, logs, stateRoot);
        Trace.Gas = gasSpent.SpentGas;
    }

    public override void StartOperation(int pc, Instruction opcode, long gas, in ExecutionEnvironment env)
    {
        FinalizePendingOpcode();

        int newDepth = env.GetGethTraceDepth();
        AdjustStorageStackForDepth(newDepth);

        _hasPendingOpcode = true;
        _pendingPc = pc;
        _pendingOpcode = opcode;
        _pendingGas = gas;
        _pendingGasCost = 0;
        _pendingDepth = newDepth;
        _pendingError = null;
        // Snapshot the cumulative refund counter before the opcode executes (geth pre-op GetRefund()).
        _pendingRefund = _refund;
        _gasCostAlreadySet = false;
        _stackByteCount = 0;
        _memoryByteCount = 0;
        _returnDataByteCount = 0;
    }

    public override void ReportOperationRemainingGas(long gas)
    {
        if (_gasCostAlreadySet || !_hasPendingOpcode) return;
        _pendingGasCost = _pendingGas - gas;
        _gasCostAlreadySet = true;
    }

    public override void ReportOperationError(EvmExceptionType error)
    {
        if (!_hasPendingOpcode) return;
        _pendingError = GetErrorDescription(error);
    }

    public override void SetOperationMemorySize(ulong newSize)
    {
        // Memory size is implicit in the data captured by SetOperationMemory — no separate field in JSON.
    }

    public override void SetOperationStack(TraceStack stack)
    {
        if (!IsTracingStack || !_hasPendingOpcode) return;
        int needed = stack.Count * EvmWordSize;
        if (needed == 0) { _stackByteCount = 0; return; }

        EnsureBuffer(ref _stackBuffer, needed);
        for (int i = 0; i < stack.Count; i++)
        {
            stack[i].Span.CopyTo(_stackBuffer.AsSpan(i * EvmWordSize, EvmWordSize));
        }
        _stackByteCount = needed;
    }

    public override void SetOperationMemory(TraceMemory memoryTrace)
    {
        if (!IsTracingFullMemory || !_hasPendingOpcode) return;
        int wordCount = (int)((memoryTrace.Size + EvmWordSize - 1) / EvmWordSize);
        if (wordCount == 0) { _memoryByteCount = 0; return; }

        int needed = wordCount * EvmWordSize;
        EnsureBuffer(ref _memoryBuffer, needed);
        Span<byte> destination = _memoryBuffer.AsSpan(0, needed);
        int copyLength = Math.Min((int)memoryTrace.Size, needed);
        if (copyLength > 0)
        {
            memoryTrace.Slice(0, copyLength, limit: false).CopyTo(destination);
        }
        if (copyLength < needed) destination[copyLength..].Clear();
        _memoryByteCount = needed;
    }

    public override void SetOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> newValue, ReadOnlySpan<byte> currentValue)
    {
        if (!IsTracingOpLevelStorage || _activeStorageDepth == 0) return;
        PooledDictionary<UInt256, UInt256> top = _storageByDepth![_activeStorageDepth - 1];
        top[storageIndex] = new UInt256(newValue, isBigEndian: true);
    }

    public override void SetOperationReturnData(ReadOnlyMemory<byte> returnData)
    {
        if (!_hasPendingOpcode) return;
        int needed = returnData.Length;
        if (needed == 0) { _returnDataByteCount = 0; return; }

        // The source buffer is reused across opcodes, so copy the bytes into our own scratch.
        EnsureBuffer(ref _returnDataBuffer, needed);
        returnData.Span.CopyTo(_returnDataBuffer.AsSpan(0, needed));
        _returnDataByteCount = needed;
    }

    public override GethLikeTxTrace BuildResult()
    {
        FinalizePendingOpcode();
        GethLikeTxTrace result = base.BuildResult();
        result.TxHash = _transaction?.Hash;
        return result;
    }

    // Dispose is left as a no-op: the per-tx `using` in TransactionProcessorAdapterExtensions
    // would otherwise release the pooled buffers between txs and defeat reuse. The owning
    // block tracer drives the real cleanup via ReleaseResources from EndBlockTrace/Dispose.

    internal void ReleaseResources()
    {
        if (_disposed) return;
        ReturnPooledBuffers();
        _disposed = true;
    }

    private void AdjustStorageStackForDepth(int newDepth)
    {
        if (!IsTracingOpLevelStorage) return;

        if (newDepth < _activeStorageDepth)
        {
            for (int i = newDepth; i < _activeStorageDepth; i++) _storageByDepth![i].Clear();
        }
        else if (newDepth > _activeStorageDepth)
        {
            _storageByDepth ??= new ArrayPoolList<PooledDictionary<UInt256, UInt256>>(InitialDepthStackCapacity);
            while (_storageByDepth.Count < newDepth)
            {
                _storageByDepth.Add(new PooledDictionary<UInt256, UInt256>(InitialStorageMapCapacity));
            }
        }

        _activeStorageDepth = newDepth;
    }

    private void EnsureBuffer(ref byte[]? buffer, int requiredLength)
    {
        if (buffer is not null && buffer.Length >= requiredLength) return;
        if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);
        buffer = ArrayPool<byte>.Shared.Rent(requiredLength);
    }

    private void ReturnPooledBuffers()
    {
        if (_stackBuffer is not null) { ArrayPool<byte>.Shared.Return(_stackBuffer); _stackBuffer = null; }
        if (_memoryBuffer is not null) { ArrayPool<byte>.Shared.Return(_memoryBuffer); _memoryBuffer = null; }
        if (_returnDataBuffer is not null) { ArrayPool<byte>.Shared.Return(_returnDataBuffer); _returnDataBuffer = null; }
        if (_returnDataHexBuffer is not null) { ArrayPool<byte>.Shared.Return(_returnDataHexBuffer); _returnDataHexBuffer = null; }
        if (_storageByDepth is not null)
        {
            for (int i = 0; i < _storageByDepth.Count; i++) _storageByDepth[i].Dispose();
            _storageByDepth.Dispose();
            _storageByDepth = null;
        }
    }

    private void FinalizePendingOpcode()
    {
        if (!_hasPendingOpcode) return;
        WriteOpcodeJson();
        _hasPendingOpcode = false;
        MaybeFlushToWire();
    }

    private void WriteOpcodeJson()
    {
        _writer.WriteStartObject();
        _writer.WriteNumber("pc"u8, _pendingPc);
        _writer.WriteString("op"u8, OpcodeJsonNames.Get(_pendingOpcode));
        _writer.WriteNumber("gas"u8, _pendingGas);
        _writer.WriteNumber("gasCost"u8, _pendingGasCost);
        _writer.WriteNumber("depth"u8, _pendingDepth);

        if (_pendingRefund != 0) _writer.WriteNumber("refund"u8, _pendingRefund);

        if (_pendingError is null) _writer.WriteNull("error"u8);
        else _writer.WriteString("error"u8, _pendingError);

        if (IsTracingStack) WriteStackArrayIfPresent();
        if (IsTracingFullMemory) WriteMemoryArrayIfPresent();
        if (IsTracingOpLevelStorage) WriteStorageObjectIfPresent();
        if (IsTracingReturnData && _returnDataByteCount > 0) WriteReturnDataValue();

        _writer.WriteEndObject();
    }

    private void WriteReturnDataValue()
    {
        // Encode "0x"-prefixed hex straight into a pooled scratch buffer and emit it as a raw JSON
        // string, avoiding the intermediate string allocation on every traced opcode after a call.
        int hexLength = _returnDataByteCount * 2;
        int tokenLength = hexLength + 4; // quotes + "0x"
        EnsureBuffer(ref _returnDataHexBuffer, tokenLength);

        Span<byte> token = _returnDataHexBuffer.AsSpan(0, tokenLength);
        token[0] = (byte)'"';
        token[1] = (byte)'0';
        token[2] = (byte)'x';
        _returnDataBuffer.AsSpan(0, _returnDataByteCount).OutputBytesToByteHex(token.Slice(3, hexLength), extraNibble: false);
        token[tokenLength - 1] = (byte)'"';

        _writer.WritePropertyName("returnData"u8);
        _writer.WriteRawValue(token, skipInputValidation: true);
    }

    private void WriteStackArrayIfPresent()
    {
        _writer.WriteStartArray("stack"u8);
        for (int offset = 0; offset < _stackByteCount; offset += EvmWordSize)
        {
            ReadOnlySpan<byte> word = _stackBuffer!.AsSpan(offset, EvmWordSize);
            HexWriter.WriteUInt256HexRawValue(_writer, new UInt256(word, isBigEndian: true));
        }
        _writer.WriteEndArray();
    }

    private void WriteMemoryArrayIfPresent()
    {
        _writer.WriteStartArray("memory"u8);
        for (int offset = 0; offset < _memoryByteCount; offset += EvmWordSize)
        {
            ReadOnlySpan<byte> slot = _memoryBuffer!.AsSpan(offset, EvmWordSize);
            if (slot.IndexOfAnyExcept((byte)0) < 0)
            {
                _writer.WriteStringValue(ZeroMemoryWord);
            }
            else
            {
                HexWriter.WriteFixed32HexRawValue(_writer, slot, addHexPrefix: false);
            }
        }
        _writer.WriteEndArray();
    }

    private void WriteStorageObjectIfPresent()
    {
        if (_activeStorageDepth == 0) return;
        PooledDictionary<UInt256, UInt256> top = _storageByDepth![_activeStorageDepth - 1];

        _writer.WriteStartObject("storage"u8);
        foreach (KeyValuePair<UInt256, UInt256> kv in top)
        {
            HexWriter.WriteUInt256HexPropertyName(_writer, kv.Key, zeroPadded: true, addHexPrefix: false);
            HexWriter.WriteUInt256HexRawValue(_writer, kv.Value, zeroPadded: true, addHexPrefix: false);
        }
        _writer.WriteEndObject();
    }

    private void MaybeFlushToWire()
    {
        if (_pipeWriter is null) return;
        if (++_entriesSinceLastFlush < _flushIntervalEntries) return;
        _writer.Flush();
        _pipeWriter.FlushAsync(_cancellationToken).SafeWait();
        _entriesSinceLastFlush = 0;
    }

    public override void ReportRefund(long refund) => _refund += refund;

    public override void ReportAction(long gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false)
    {
        base.ReportAction(gas, value, from, to, input, callType, isPrecompileCall);
        _refundCheckpoints.Push(_refund);
    }

    public override void ReportActionEnd(long gas, ReadOnlyMemory<byte> output)
    {
        base.ReportActionEnd(gas, output);
        _refundCheckpoints.TryPop(out _);
    }

    public override void ReportActionEnd(long gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode)
    {
        base.ReportActionEnd(gas, deploymentAddress, deployedCode);
        _refundCheckpoints.TryPop(out _);
    }

    public override void ReportActionRevert(long gasLeft, ReadOnlyMemory<byte> output)
    {
        base.ReportActionRevert(gasLeft, output);
        RestoreRefundCheckpoint();
    }

    public override void ReportActionError(EvmExceptionType evmExceptionType)
    {
        base.ReportActionError(evmExceptionType);
        RestoreRefundCheckpoint();
    }

    // A reverted or aborted frame rolls back every refund accrued within it (and its successful
    // children), mirroring go-ethereum's journaled refund counter.
    private void RestoreRefundCheckpoint()
    {
        if (_refundCheckpoints.TryPop(out long checkpoint))
            _refund = checkpoint;
    }
}
