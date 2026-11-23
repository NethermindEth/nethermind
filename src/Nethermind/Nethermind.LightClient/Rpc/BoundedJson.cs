// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.JsonRpc;
using Nethermind.Serialization.Json;

namespace Nethermind.LightClient.Rpc;

internal static class BoundedJson
{
    internal const int ResultLimit = 8 * 1024 * 1024;
    private static readonly ConditionalWeakTable<JsonSerializerOptions, JsonSerializerOptions> Options = [];
    internal static readonly SerializedJson Null = new("null"u8.ToArray());

    internal static SerializedJson Serialize<T>(T value)
    {
        using BoundedJsonBuffer output = new(ResultLimit);
        Write(output, value);
        return new(output.WrittenMemory.ToArray());
    }

    internal static JsonElement ToElement<T>(T value)
    {
        using BoundedJsonBuffer output = new(ResultLimit);
        Write(output, value);
        return JsonElement.Parse(output.WrittenMemory.Span);
    }

    private static void Write<T>(IBufferWriter<byte> output, T value)
    {
        JsonSerializerOptions options = EthereumJsonSerializer.JsonOptions;
        using Utf8JsonWriter writer = new(output, new() { Encoder = options.Encoder });
        TypeInfoJsonSerializer.Serialize(writer, value, Options.GetValue(options, CreateOptions));
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

/// <summary>Bounds committed UTF-8 output while allowing temporary JSON-writer reservations.</summary>
internal sealed class BoundedJsonBuffer(int limit, int reservationSlack = 4096, string? limitMessage = null) : IBufferWriter<byte>, IDisposable
{
    private byte[] _buffer = [];
    private int _written;
    private bool _disposed;
    internal int WriteLimit { private get; set; } = limit;
    internal ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _written);

    public void Advance(int count)
    {
        if (count < 0 || count > WriteLimit - _written) throw TooLarge();
        _written += count;
    }

    internal void Rewind(int position) => _written = position;

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsMemory(_written, Math.Min(_buffer.Length, WriteLimit + reservationSlack) - _written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsSpan(_written, Math.Min(_buffer.Length, WriteLimit + reservationSlack) - _written);
    }

    private void EnsureCapacity(int sizeHint)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int maximum = WriteLimit + reservationSlack;
        if (sizeHint < 0 || sizeHint > maximum - _written) throw TooLarge();
        int needed = _written + Math.Max(1, sizeHint);
        if (needed <= _buffer.Length) return;
        byte[] replacement = ArrayPool<byte>.Shared.Rent(Math.Min(maximum, Math.Max(needed, Math.Max(4096, _buffer.Length * 2))));
        _buffer.AsSpan(0, _written).CopyTo(replacement);
        if (_buffer.Length != 0) ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = replacement;
    }

    private RpcException TooLarge() => limitMessage is null ? BoundedJson.TooLarge() : new(-32005, limitMessage);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_buffer.Length != 0) ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = [];
    }
}
