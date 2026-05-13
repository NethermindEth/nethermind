// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Core.BlockAccessLists;

public sealed class IndexedChangesJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType &&
        typeToConvert.GetGenericTypeDefinition() == typeof(IndexedChanges<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        Type changeType = typeToConvert.GetGenericArguments()[0];
        return typeof(IIndexedChange).IsAssignableFrom(changeType)
            ? (JsonConverter)Activator.CreateInstance(typeof(IndexedChangesJsonConverter<>).MakeGenericType(changeType))!
            : ThrowInvalidChangeType(changeType);
    }

    [DoesNotReturn, StackTraceHidden]
    private static JsonConverter ThrowInvalidChangeType(Type changeType) =>
        throw new InvalidOperationException($"{changeType.Name} must implement {nameof(IIndexedChange)}.");
}

internal sealed class IndexedChangesJsonConverter<T> : JsonConverter<IndexedChanges<T>>
    where T : struct, IIndexedChange
{
    public override IndexedChanges<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        ThrowReadNotSupported();

    public override void Write(Utf8JsonWriter writer, IndexedChanges<T> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (KeyValuePair<uint, T> change in value)
        {
            WriteIndexPropertyName(writer, change.Key);
            JsonSerializer.Serialize(writer, change.Value, options);
        }
        writer.WriteEndObject();
    }

    private static void WriteIndexPropertyName(Utf8JsonWriter writer, uint index)
    {
        Span<char> buffer = stackalloc char[10];
        if (index.TryFormat(buffer, out int charsWritten, provider: CultureInfo.InvariantCulture))
        {
            writer.WritePropertyName(buffer[..charsWritten]);
            return;
        }

        ThrowIndexFormatFailed();
    }

    [DoesNotReturn, StackTraceHidden]
    private static IndexedChanges<T> ThrowReadNotSupported() =>
        throw new NotSupportedException($"{nameof(IndexedChanges<T>)} JSON deserialization is not supported.");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowIndexFormatFailed() =>
        throw new InvalidOperationException("Could not format block access index.");
}
