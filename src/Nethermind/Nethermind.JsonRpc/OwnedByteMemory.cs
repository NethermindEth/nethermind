// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc;

/// <summary>Holds byte memory that must stay alive until the JSON-RPC response has been serialized.</summary>
[JsonConverter(typeof(OwnedByteMemoryConverter))]
public sealed class OwnedByteMemory : IDisposable
{
    private readonly MemoryManager<byte> _memoryManager;

    public OwnedByteMemory(MemoryManager<byte> memoryManager)
    {
        ArgumentNullException.ThrowIfNull(memoryManager);
        _memoryManager = memoryManager;
    }

    /// <summary>Gets the owned bytes to serialize.</summary>
    public Memory<byte> Memory => _memoryManager.Memory;

    public void Dispose() => ((IDisposable)_memoryManager).Dispose();
}

internal sealed class OwnedByteMemoryConverter : JsonConverter<OwnedByteMemory>
{
    public override OwnedByteMemory Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException($"{nameof(OwnedByteMemoryConverter)} is serialize-only");

    public override void Write(Utf8JsonWriter writer, OwnedByteMemory value, JsonSerializerOptions options) =>
        ByteArrayConverter.Convert(writer, value.Memory.Span, skipLeadingZeros: false);
}
