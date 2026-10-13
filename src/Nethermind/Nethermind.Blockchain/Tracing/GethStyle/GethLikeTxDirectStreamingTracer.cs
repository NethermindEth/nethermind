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
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Nethermind.Blockchain.Tracing.GethStyle;

/// <summary>
/// Streams Geth-style struct-log entries directly to a <see cref="Utf8JsonWriter"/> without allocating
/// per-opcode <see cref="GethTxMemoryTraceEntry"/> objects. Peak memory is bounded by a single opcode's
/// stack/memory plus, when storage tracing is enabled, the cumulative per-address storage map for the
/// transaction.
/// </summary>
/// <remarks>
/// The pipe is flushed whenever the unflushed output reaches the flush threshold, including partway through
/// a large memory or storage dump, so a slow reader holds execution back instead of letting the response
/// buffer grow with the size of each entry.
/// </remarks>
public sealed class GethLikeTxDirectStreamingTracer : GethLikeTxTracer
{
    internal const int DefaultFlushThresholdBytes = 1024 * 1024;
    private const int EvmWordSize = EvmPooledMemory.WordSize;
    private static readonly JsonEncodedText ZeroMemoryWord = JsonEncodedText.Encode("0x" + new string('0', EvmWordSize * 2));
    private const int InitialStorageMapCapacity = 8;
    private const int ReturnDataHexChunkBytes = 1024;

    private readonly Utf8JsonWriter _writer;
    private readonly PipeWriter? _pipeWriter;
    private readonly CancellationToken _cancellationToken;
    private Transaction? _transaction;
    private readonly int _flushThresholdBytes;

    private bool _hasPendingOpcode;
    private int _pendingPc;
    private Instruction _pendingOpcode;
    private ulong _pendingGas;
    private ulong _pendingGasCost;
    private int _pendingDepth;
    private string? _pendingError;
    private long _pendingRefund;
    private bool _gasCostAlreadySet;
    private bool _pendingStorageTouched;

    private byte[]? _stackBuffer;
    private int _stackByteCount;

    private byte[]? _memoryBuffer;
    private int _memoryByteCount;

    private byte[]? _returnDataBuffer;
    private int _returnDataByteCount;

    private readonly PooledDictionary<AddressAsKey, PooledDictionary<UInt256, UInt256>> _storageByAddress = [with(InitialStorageMapCapacity)];
    private readonly Stack<PooledDictionary<UInt256, UInt256>> _storageMapPool = new();
    private PooledDictionary<UInt256, UInt256>? _pendingStorageMap;

    private readonly long _limit;
    private long _resultSize;
    private long _flushedBytes;
    private bool _disposed;

    public GethLikeTxDirectStreamingTracer(
        Transaction? transaction,
        GethTraceOptions options,
        Utf8JsonWriter writer,
        PipeWriter? pipeWriter,
        CancellationToken cancellationToken,
        int flushThresholdBytes = DefaultFlushThresholdBytes,
        long destroyRefund = 0)
        : base(options, destroyRefund)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (flushThresholdBytes <= 0) throw new ArgumentOutOfRangeException(nameof(flushThresholdBytes));

        _transaction = transaction;
        _limit = options.Limit;
        _writer = writer;
        _pipeWriter = pipeWriter;
        _cancellationToken = cancellationToken;
        _flushThresholdBytes = flushThresholdBytes;
        _flushedBytes = writer.BytesCommitted;
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
        _pendingStorageTouched = false;
        ResetRefund();
        _stackByteCount = 0;
        _memoryByteCount = 0;
        _returnDataByteCount = 0;
        foreach (PooledDictionary<UInt256, UInt256> map in _storageByAddress.Values)
        {
            map.Clear();
            _storageMapPool.Push(map);
        }
        _storageByAddress.Clear();
        _pendingStorageMap = null;
        _resultSize = 0;
        ResetTrace();
    }

    public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
    {
        base.MarkAsSuccess(recipient, gasSpent, output, logs, stateRoot);
        Trace.Gas = gasSpent.SpentGas;
    }

    public override void StartOperation(int pc, Instruction opcode, ulong gas, in ExecutionEnvironment env)
    {
        FinalizePendingOpcode();
        if (_limit != 0 && _resultSize > _limit) return;

        _hasPendingOpcode = true;
        _pendingPc = pc;
        _pendingOpcode = opcode;
        _pendingGas = gas;
        _pendingGasCost = 0;
        _pendingDepth = env.GetGethTraceDepth();
        _pendingError = null;
        _pendingRefund = CurrentRefund;
        _gasCostAlreadySet = false;
        _pendingStorageTouched = false;
        _pendingStorageMap = null;
        _stackByteCount = 0;
        _memoryByteCount = 0;
        _returnDataByteCount = 0;
    }

    public override void ReportOperationRemainingGas(ulong gas)
    {
        if (_gasCostAlreadySet || !_hasPendingOpcode) return;
        _pendingGasCost = _pendingGas - gas;
        _pendingRefund = CurrentRefund;
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

    public override void SetOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> newValue, ReadOnlySpan<byte> currentValue) =>
        RecordStorage(address, storageIndex, newValue);

    public override void LoadOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> value) =>
        RecordStorage(address, storageIndex, value);

    private void RecordStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> value)
    {
        if (!IsTracingOpLevelStorage || !_hasPendingOpcode) return;
        if (!_storageByAddress.TryGetValue(address, out PooledDictionary<UInt256, UInt256>? contractStorage))
        {
            contractStorage = _storageMapPool.TryPop(out PooledDictionary<UInt256, UInt256>? pooled)
                ? pooled
                : [with(InitialStorageMapCapacity)];
            _storageByAddress[address] = contractStorage;
        }
        contractStorage[storageIndex] = new UInt256(value, isBigEndian: true);
        _pendingStorageMap = contractStorage;
        _pendingStorageTouched = true;
    }

    public override void SetOperationReturnData(ReadOnlySpan<byte> returnData)
    {
        if (!_hasPendingOpcode) return;
        int needed = returnData.Length;
        if (needed == 0) { _returnDataByteCount = 0; return; }

        // The source buffer is reused across opcodes, so copy the bytes into our own scratch.
        EnsureBuffer(ref _returnDataBuffer, needed);
        returnData.CopyTo(_returnDataBuffer.AsSpan(0, needed));
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
        foreach (PooledDictionary<UInt256, UInt256> map in _storageByAddress.Values) map.Dispose();
        _storageByAddress.Dispose();

        while (_storageMapPool.TryPop(out PooledDictionary<UInt256, UInt256>? map)) map.Dispose();

        _pendingStorageMap = null;
    }

    private void FinalizePendingOpcode()
    {
        if (!_hasPendingOpcode) return;
        WriteOpcodeJson();
        _hasPendingOpcode = false;
        FlushToWireIfOverThreshold();
    }

    private void WriteOpcodeJson()
    {
        _writer.WriteStartObject();
        // Exclude the array separator: Geth counts individually serialized log objects.
        long entryStart = _writer.BytesCommitted + _writer.BytesPending - 1;
        _writer.WriteNumber("pc"u8, _pendingPc);
        _writer.WriteString("op"u8, OpcodeJsonNames.Get(_pendingOpcode));
        _writer.WriteNumber("gas"u8, _pendingGas);
        _writer.WriteNumber("gasCost"u8, _pendingGasCost);
        _writer.WriteNumber("depth"u8, _pendingDepth);

        if (_pendingRefund != 0) _writer.WriteNumber("refund"u8, _pendingRefund);

        if (_pendingError is not null) _writer.WriteString("error"u8, _pendingError);

        if (IsTracingStack) WriteStackArrayIfPresent();
        if (IsTracingFullMemory && _memoryByteCount > 0) WriteMemoryArrayIfPresent();
        if (IsTracingOpLevelStorage && _pendingStorageTouched) WriteStorageObjectIfPresent();
        if (IsTracingReturnData && _returnDataByteCount > 0) WriteReturnDataValue();

        _writer.WriteEndObject();
        if (_limit > 0)
            _resultSize += _writer.BytesCommitted + _writer.BytesPending - entryStart;
    }

    private void WriteReturnDataValue()
    {
        // Hex-encode in fixed-size string segments so a large return value is flushed as it is written instead of
        // being materialized as one token first; hex digits are never escaped, so the bytes match a single string.
        Span<byte> hex = stackalloc byte[Math.Min(_returnDataByteCount, ReturnDataHexChunkBytes) * 2];
        _writer.WritePropertyName("returnData"u8);
        _writer.WriteStringValueSegment("0x"u8, isFinalSegment: false);
        ReadOnlySpan<byte> remaining = _returnDataBuffer.AsSpan(0, _returnDataByteCount);
        while (remaining.Length > ReturnDataHexChunkBytes)
        {
            remaining[..ReturnDataHexChunkBytes].OutputBytesToByteHex(hex, extraNibble: false);
            _writer.WriteStringValueSegment(hex, isFinalSegment: false);
            remaining = remaining[ReturnDataHexChunkBytes..];
            FlushToWireIfOverThreshold();
        }
        Span<byte> last = hex[..(remaining.Length * 2)];
        remaining.OutputBytesToByteHex(last, extraNibble: false);
        _writer.WriteStringValueSegment(last, isFinalSegment: true);
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
                HexWriter.WriteFixed32HexRawValue(_writer, slot, addHexPrefix: true);
            }
            FlushToWireIfOverThreshold();
        }
        _writer.WriteEndArray();
    }

    private void WriteStorageObjectIfPresent()
    {
        _writer.WriteStartObject("storage"u8);
        foreach (KeyValuePair<UInt256, UInt256> kv in _pendingStorageMap!)
        {
            HexWriter.WriteUInt256StorageSlot(_writer, kv.Key, kv.Value);
            FlushToWireIfOverThreshold();
        }
        _writer.WriteEndObject();
    }

    private void FlushToWireIfOverThreshold()
    {
        if (_pipeWriter is null || _writer.BytesCommitted + _writer.BytesPending - _flushedBytes < _flushThresholdBytes) return;
        _writer.Flush();
        _pipeWriter.FlushAsync(_cancellationToken).SafeWait();
        _flushedBytes = _writer.BytesCommitted;
    }

}
