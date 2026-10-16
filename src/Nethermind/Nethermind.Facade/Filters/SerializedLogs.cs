// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Json;

namespace Nethermind.Facade.Filters;

/// <summary>Consecutive logs, each serialized as the JSON object of its <see cref="FilterLog"/>.</summary>
/// <remarks>The JSON lives in buffers rented from <see cref="ArrayPool{T}.Shared"/> until <see cref="Dispose"/>.</remarks>
public sealed class SerializedLogs : IBufferWriter<byte>, IDisposable
{
    private const int InitialBufferSize = 4096;
    private const int InitialLogCapacity = 16;
    private const int MaxStackDataLength = 256;

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        SkipValidation = true,
        Encoder = EthereumJsonSerializer.JsonOptions.Encoder,
        MaxDepth = EthereumJsonSerializer.JsonOptions.MaxDepth
    };

    private readonly Utf8JsonWriter _writer;
    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(InitialBufferSize);
    private int _written;
    private int[] _ends = ArrayPool<int>.Shared.Rent(InitialLogCapacity);

    internal SerializedLogs() => _writer = new Utf8JsonWriter(this, WriterOptions);

    public int Count { get; private set; }

    /// <summary>Gets the JSON object of the log at <paramref name="index"/>.</summary>
    public ReadOnlySpan<byte> this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)Count, nameof(index));
            int start = index == 0 ? 0 : _ends[index - 1];
            return _buffer.AsSpan(start, _ends[index] - start);
        }
    }

    internal void Add(FilterLog log)
    {
        TypeInfoJsonSerializer.Serialize(_writer, log, EthereumJsonSerializer.JsonOptions);
        CompleteLog();
    }

    /// <summary>Writes the log exactly as the JSON writer of <see cref="FilterLog"/> would, without materializing it.</summary>
    /// <exception cref="ArgumentException"><paramref name="transactionHash"/> is not <see cref="Hash256.Size"/> bytes, as when it was not recovered.</exception>
    internal void Add(
        long logIndex,
        ulong blockNumber,
        ulong blockTimestamp,
        Hash256 blockHash,
        long transactionIndex,
        ReadOnlySpan<byte> transactionHash,
        scoped in LogEntryStructRef log,
        Hash256[] topics)
    {
        if (transactionHash.Length != Hash256.Size)
        {
            ThrowInvalidTransactionHash(transactionHash.Length);
        }

        Utf8JsonWriter writer = _writer;
        writer.WriteStartObject();
        writer.WritePropertyName("address"u8);
        ByteArrayConverter.Convert(writer, log.Address.Bytes, skipLeadingZeros: false);
        writer.WritePropertyName("blockHash"u8);
        HexWriter.WriteFixed32HexRawValue(writer, blockHash.Bytes);
        writer.WritePropertyName("blockNumber"u8);
        NumericConverterHelper.Write(writer, blockNumber);
        writer.WritePropertyName("blockTimestamp"u8);
        NumericConverterHelper.Write(writer, blockTimestamp);
        writer.WritePropertyName("data"u8);
        WriteData(writer, log);
        writer.WritePropertyName("logIndex"u8);
        NumericConverterHelper.Write(writer, logIndex);
        writer.WriteBoolean("removed"u8, false);
        writer.WritePropertyName("topics"u8);
        writer.WriteStartArray();
        foreach (Hash256 topic in topics)
        {
            HexWriter.WriteFixed32HexRawValue(writer, topic.Bytes);
        }
        writer.WriteEndArray();
        writer.WritePropertyName("transactionHash"u8);
        HexWriter.WriteFixed32HexRawValue(writer, transactionHash);
        writer.WritePropertyName("transactionIndex"u8);
        NumericConverterHelper.Write(writer, transactionIndex);
        writer.WriteEndObject();
        CompleteLog();
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowInvalidTransactionHash(int length) =>
        throw new ArgumentException($"Transaction hash must be {Hash256.Size} bytes and was {length} bytes", "transactionHash");

    private static void WriteData(Utf8JsonWriter writer, scoped in LogEntryStructRef log)
    {
        int length = log.DataLength;
        byte[]? rented = length > MaxStackDataLength ? ArrayPool<byte>.Shared.Rent(length) : null;
        Span<byte> data = (rented is null ? stackalloc byte[MaxStackDataLength] : rented)[..length];
        log.CopyDataTo(data);
        ByteArrayConverter.Convert(writer, data, skipLeadingZeros: false);
        if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
    }

    private void CompleteLog()
    {
        _writer.Flush();
        // Each log is a separate top-level value; a reset keeps the writer from separating them with a comma.
        _writer.Reset(this);
        if (Count == _ends.Length)
        {
            _ends = Grow(_ends, Count + 1, Count);
        }

        _ends[Count++] = _written;
    }

    public void Dispose()
    {
        if (_buffer.Length == 0) return;

        _writer.Dispose();
        ArrayPool<byte>.Shared.Return(_buffer);
        ArrayPool<int>.Shared.Return(_ends);
        _buffer = [];
        _ends = [];
        _written = 0;
        Count = 0;
    }

    void IBufferWriter<byte>.Advance(int count) => _written += count;

    Memory<byte> IBufferWriter<byte>.GetMemory(int sizeHint) => EnsureCapacity(sizeHint).AsMemory(_written);

    Span<byte> IBufferWriter<byte>.GetSpan(int sizeHint) => EnsureCapacity(sizeHint).AsSpan(_written);

    private byte[] EnsureCapacity(int sizeHint)
    {
        int required = _written + Math.Max(sizeHint, 1);
        if (required > _buffer.Length)
        {
            _buffer = Grow(_buffer, required, _written);
        }

        return _buffer;
    }

    private static T[] Grow<T>(T[] array, int required, int used)
    {
        T[] grown = ArrayPool<T>.Shared.Rent(Math.Max(required, array.Length * 2));
        array.AsSpan(0, used).CopyTo(grown);
        ArrayPool<T>.Shared.Return(array);
        return grown;
    }
}
