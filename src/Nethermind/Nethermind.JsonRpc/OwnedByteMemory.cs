// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc;

/// <summary>
/// Holds byte memory that must stay alive until the JSON-RPC response has been serialized.
/// </summary>
[JsonConverter(typeof(OwnedByteMemoryConverter))]
public sealed class OwnedByteMemory : IDisposable
{
    private byte[]? _pooledArray;
    private IDisposable? _manager;
    private readonly Memory<byte> _memory;
    private readonly ArrayPool<byte>? _pool;

    public OwnedByteMemory(MemoryManager<byte> memoryManager)
    {
        ArgumentNullException.ThrowIfNull(memoryManager);
        _manager = memoryManager;
        _memory = memoryManager.Memory;
    }

    public OwnedByteMemory(byte[] pooledBuffer, int length, ArrayPool<byte> pool)
    {
        ArgumentNullException.ThrowIfNull(pooledBuffer);
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, pooledBuffer.Length, nameof(length));

        _pooledArray = pooledBuffer;
        _memory = pooledBuffer.AsMemory(0, length);
        _pool = pool;
    }

    /// <summary>
    /// Gets the owned bytes to serialize.
    /// </summary>
    public Memory<byte> Memory
    {
        get
        {
            ObjectDisposedException.ThrowIf(_pooledArray is null && _manager is null, this);
            return _memory;
        }
    }

    public void Dispose()
    {
        if (_pool is not null)
        {
            byte[]? array = Interlocked.Exchange(ref _pooledArray, null);
            if (array is not null) _pool.Return(array);
        }
        else
        {
            IDisposable? manager = Interlocked.Exchange(ref _manager, null);
            manager?.Dispose();
        }
    }
}

internal sealed class OwnedByteMemoryConverter : JsonConverter<OwnedByteMemory>
{
    public override OwnedByteMemory Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException($"{nameof(OwnedByteMemoryConverter)} is serialize-only");

    public override void Write(Utf8JsonWriter writer, OwnedByteMemory value, JsonSerializerOptions options) =>
        ByteArrayConverter.Convert(writer, value.Memory.Span, skipLeadingZeros: false);
}
