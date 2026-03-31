// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.Modules.Eth;

[JsonConverter(typeof(StorageValuesResultConverter))]
public sealed class StorageValuesResult : IDisposable
{
    private readonly byte[] _buffer;

    public StorageValuesResult(byte[] buffer, Dictionary<Address, Memory<byte>[]> slots)
    {
        _buffer = buffer;
        Slots = slots;
    }

    public Dictionary<Address, Memory<byte>[]> Slots { get; }

    public void Dispose() => ArrayPool<byte>.Shared.Return(_buffer);
}

internal sealed class StorageValuesResultConverter : JsonConverter<StorageValuesResult>
{
    public override StorageValuesResult Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException($"{nameof(StorageValuesResultConverter)} is serialize-only");

    [SkipLocalsInit]
    public override void Write(Utf8JsonWriter writer, StorageValuesResult value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        Span<byte> keyBytes = stackalloc byte[Address.Size * 2 + 2];
        keyBytes[0] = (byte)'0';
        keyBytes[1] = (byte)'x';
        foreach (KeyValuePair<Address, Memory<byte>[]> entry in value.Slots)
        {
            entry.Key.Bytes.AsSpan().OutputBytesToByteHex(keyBytes[2..], false);
            writer.WritePropertyName(keyBytes);
            writer.WriteStartArray();
            foreach (Memory<byte> slot in entry.Value)
            {
                ByteArrayConverter.Convert(writer, slot.Span, skipLeadingZeros: false);
            }
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }
}
