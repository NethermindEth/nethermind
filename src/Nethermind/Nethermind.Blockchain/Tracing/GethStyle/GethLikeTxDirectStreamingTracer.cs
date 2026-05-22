// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Text.Json;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

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
    private const int InitialDepthStackCapacity = 4;

    private readonly Utf8JsonWriter _writer;
    private readonly PipeWriter? _pipeWriter;
    private readonly CancellationToken _cancellationToken;
    private readonly Transaction? _transaction;
    private readonly int _flushIntervalEntries;

    private bool _hasPendingOpcode;
    private int _pendingPc;
    private Instruction _pendingOpcode;
    private long _pendingGas;
    private long _pendingGasCost;
    private int _pendingDepth;
    private string? _pendingError;
    private bool _gasCostAlreadySet;

    private byte[]? _stackBuffer;
    private int _stackByteCount;

    private byte[]? _memoryBuffer;
    private int _memoryByteCount;

    private readonly Stack<Dictionary<UInt256, UInt256>> _storageByDepth = new(InitialDepthStackCapacity);

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
        _gasCostAlreadySet = false;
        _stackByteCount = 0;
        _memoryByteCount = 0;
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
        if (!IsTracingOpLevelStorage || _storageByDepth.Count == 0) return;
        Dictionary<UInt256, UInt256> top = _storageByDepth.Peek();
        top[storageIndex] = new UInt256(newValue, isBigEndian: true);
    }

    public override GethLikeTxTrace BuildResult()
    {
        FinalizePendingOpcode();
        ReturnPooledBuffers();
        GethLikeTxTrace result = base.BuildResult();
        result.TxHash = _transaction?.Hash;
        return result;
    }

    public override void Dispose()
    {
        if (_disposed) return;
        ReturnPooledBuffers();
        _disposed = true;
        base.Dispose();
    }

    private void AdjustStorageStackForDepth(int newDepth)
    {
        if (!IsTracingOpLevelStorage) return;
        while (_storageByDepth.Count > newDepth) _storageByDepth.Pop();
        while (_storageByDepth.Count < newDepth) _storageByDepth.Push(new Dictionary<UInt256, UInt256>(InitialStorageMapCapacity));
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

        if (_pendingError is null) _writer.WriteNull("error"u8);
        else _writer.WriteString("error"u8, _pendingError);

        if (IsTracingStack) WriteStackArrayIfPresent();
        if (IsTracingFullMemory) WriteMemoryArrayIfPresent();
        if (IsTracingOpLevelStorage) WriteStorageObjectIfPresent();

        _writer.WriteEndObject();
    }

    private void WriteStackArrayIfPresent()
    {
        _writer.WriteStartArray("stack"u8);
        Span<byte> hexBuffer = stackalloc byte[2 + EvmWordSize * 2];
        for (int offset = 0; offset < _stackByteCount; offset += EvmWordSize)
        {
            int written = FormatHexAscii(_stackBuffer!.AsSpan(offset, EvmWordSize), hexBuffer, withPrefix: true, trimLeadingZeros: true);
            _writer.WriteStringValue(hexBuffer[..written]);
        }
        _writer.WriteEndArray();
    }

    private void WriteMemoryArrayIfPresent()
    {
        _writer.WriteStartArray("memory"u8);
        Span<byte> hexBuffer = stackalloc byte[EvmWordSize * 2];
        for (int offset = 0; offset < _memoryByteCount; offset += EvmWordSize)
        {
            ReadOnlySpan<byte> slot = _memoryBuffer!.AsSpan(offset, EvmWordSize);
            if (IsAllZero(slot))
            {
                _writer.WriteStringValue(ZeroMemoryWord);
            }
            else
            {
                slot.OutputBytesToByteHex(hexBuffer, extraNibble: false);
                _writer.WriteStringValue(hexBuffer);
            }
        }
        _writer.WriteEndArray();
    }

    private static bool IsAllZero(ReadOnlySpan<byte> slot)
    {
        for (int i = 0; i < slot.Length; i++) if (slot[i] != 0) return false;
        return true;
    }

    private void WriteStorageObjectIfPresent()
    {
        if (_storageByDepth.Count == 0) return;
        Dictionary<UInt256, UInt256> top = _storageByDepth.Peek();

        _writer.WriteStartObject("storage"u8);
        Span<byte> keyBytes = stackalloc byte[EvmWordSize];
        Span<byte> valueBytes = stackalloc byte[EvmWordSize];
        Span<byte> keyHex = stackalloc byte[EvmWordSize * 2];
        Span<byte> valueHex = stackalloc byte[EvmWordSize * 2];
        foreach (KeyValuePair<UInt256, UInt256> kv in top)
        {
            kv.Key.ToBigEndian(keyBytes);
            ((ReadOnlySpan<byte>)keyBytes).OutputBytesToByteHex(keyHex, extraNibble: false);
            _writer.WritePropertyName(keyHex);

            kv.Value.ToBigEndian(valueBytes);
            ((ReadOnlySpan<byte>)valueBytes).OutputBytesToByteHex(valueHex, extraNibble: false);
            _writer.WriteStringValue(valueHex);
        }
        _writer.WriteEndObject();
    }

    private void MaybeFlushToWire()
    {
        if (_pipeWriter is null) return;
        if (++_entriesSinceLastFlush < _flushIntervalEntries) return;
        _writer.Flush();
        _pipeWriter.FlushAsync(_cancellationToken).GetAwaiter().GetResult();
        _entriesSinceLastFlush = 0;
    }

    internal static int FormatHexAscii(ReadOnlySpan<byte> source, Span<byte> destination, bool withPrefix, bool trimLeadingZeros)
    {
        int outIdx = 0;
        int start = 0;
        bool emittedAny = false;

        if (withPrefix)
        {
            destination[outIdx++] = (byte)'0';
            destination[outIdx++] = (byte)'x';
        }

        if (trimLeadingZeros)
        {
            while (start < source.Length && source[start] == 0) start++;
            if (start < source.Length)
            {
                byte first = source[start];
                if (first <= 0xF)
                {
                    destination[outIdx++] = NibbleAscii(first);
                    emittedAny = true;
                    start++;
                }
            }
        }

        for (int i = start; i < source.Length; i++)
        {
            byte b = source[i];
            destination[outIdx++] = NibbleAscii((b >> 4) & 0xF);
            destination[outIdx++] = NibbleAscii(b & 0xF);
            emittedAny = true;
        }

        if (trimLeadingZeros && !emittedAny)
        {
            destination[outIdx++] = (byte)'0';
        }

        return outIdx;
    }

    private static byte NibbleAscii(int nibble) =>
        nibble < 10 ? (byte)('0' + nibble) : (byte)('a' + (nibble - 10));
}
