// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Serialization.Json;

namespace Nethermind.LightClient;

internal static class BoundedJson
{
    internal const int ResultLimit = 8 * 1024 * 1024;
    private static readonly ConditionalWeakTable<JsonSerializerOptions, JsonSerializerOptions> Options = [];

    internal static JsonElement ToElement<T>(T value)
    {
        using BoundedJsonBuffer output = new(ResultLimit);
        using (Utf8JsonWriter writer = new(output))
            TypeInfoJsonSerializer.Serialize(writer, value, Options.GetValue(EthereumJsonSerializer.JsonOptions, CreateOptions));
        using JsonDocument document = JsonDocument.Parse(output.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static JsonSerializerOptions CreateOptions(JsonSerializerOptions original)
    {
        JsonSerializerOptions options = new(original);
        options.Converters.Insert(0, new ByteArrayWriter());
        options.Converters.Insert(0, new ByteArrayArrayWriter());
        options.Converters.Insert(0, new MemoryWriter());
        return options;
    }

    private static void WriteHex(Utf8JsonWriter writer, ReadOnlySpan<byte> bytes)
    {
        long length = bytes.Length * 2L + 4;
        if (length > ResultLimit - writer.BytesCommitted - writer.BytesPending) throw TooLarge();
        HexWriter.WriteHexStringValue(writer, bytes);
    }

    internal static RpcException TooLarge() => new(-32005, "Verified JSON result exceeds the 8 MiB response limit.");

    private sealed class ByteArrayWriter : JsonConverter<byte[]>
    {
        public override byte[]? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => ByteArrayConverter.Convert(ref reader);
        public override void Write(Utf8JsonWriter writer, byte[] bytes, JsonSerializerOptions options) => WriteHex(writer, bytes);
    }

    private sealed class ByteArrayArrayWriter : JsonConverter<byte[]?[]>
    {
        public override byte[]?[] Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, byte[]?[] values, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (byte[]? value in values)
            {
                if (value is null) writer.WriteNullValue();
                else WriteHex(writer, value);
            }
            writer.WriteEndArray();
        }
    }

    private sealed class MemoryWriter : JsonConverter<ReadOnlyMemory<byte>>
    {
        public override ReadOnlyMemory<byte> Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => ByteArrayConverter.Convert(ref reader);
        public override void Write(Utf8JsonWriter writer, ReadOnlyMemory<byte> bytes, JsonSerializerOptions options) => WriteHex(writer, bytes.Span);
    }
}

/// <summary>Bounds committed UTF-8 output while allowing the JSON writer's final buffer reservation.</summary>
internal sealed class BoundedJsonBuffer(int limit) : IBufferWriter<byte>, IDisposable
{
    private const int ReservationSlack = 4096;
    private byte[] _buffer = [];
    private int _written;
    private bool _disposed;
    internal ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _written);

    public void Advance(int count)
    {
        if (count < 0 || count > limit - _written) throw BoundedJson.TooLarge();
        _written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsMemory(_written, Math.Min(_buffer.Length, limit + ReservationSlack) - _written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsSpan(_written, Math.Min(_buffer.Length, limit + ReservationSlack) - _written);
    }

    private void EnsureCapacity(int sizeHint)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int maximum = limit + ReservationSlack;
        if (sizeHint < 0 || sizeHint > maximum - _written) throw BoundedJson.TooLarge();
        int needed = _written + Math.Max(1, sizeHint);
        if (needed <= _buffer.Length) return;
        byte[] replacement = ArrayPool<byte>.Shared.Rent(Math.Min(maximum, Math.Max(needed, Math.Max(4096, _buffer.Length * 2))));
        _buffer.AsSpan(0, _written).CopyTo(replacement);
        if (_buffer.Length != 0) ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = replacement;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_buffer.Length != 0) ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = [];
    }
}
