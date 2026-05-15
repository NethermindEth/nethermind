// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Text.Json;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Ssz;

namespace Nethermind.FastRpc.Benchmark;

internal sealed class BenchmarkPayload(string name, byte[] json, byte[] ssz)
{
    public string Name { get; } = name;
    public byte[] Json { get; } = json;
    public byte[] Ssz { get; } = ssz;
}

internal static class BenchmarkPayloads
{
    public const string Simple = "bench_simple";
    public const string Complex = "bench_complex";
    public const string Big = "bench_big";

    public static BenchmarkPayload Create(string name) =>
        name switch
        {
            Simple => FromValue(name, new FastRpcSimpleObject
            {
                Id = 1,
                Count = 42,
                Enabled = true,
            }),
            Complex => FromValue(name, CreateComplexObject(items: 16, dataBytes: 8 * 1024)),
            Big => FromValue(name, CreateBigObject(items: 512, dataBytes: 512 * 1024)),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown benchmark payload."),
        };

    public static BenchmarkPayload[] CreateAll() =>
    [
        Create(Simple),
        Create(Complex),
        Create(Big),
    ];

    private static BenchmarkPayload FromValue<T>(string name, T value) where T : ISszCodec<T>
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(value, EthereumJsonSerializer.JsonOptions);
        byte[] ssz = T.Encode(value);
        return new BenchmarkPayload(name, json, ssz);
    }

    private static FastRpcComplexObject CreateComplexObject(int items, int dataBytes)
    {
        FastRpcItem[] itemArray = CreateItems(items);
        byte[] payload = CreateBytes(dataBytes);
        return new FastRpcComplexObject
        {
            Id = 2,
            Score = 100,
            Items = itemArray,
            Payload = payload,
        };
    }

    private static FastRpcBigObject CreateBigObject(int items, int dataBytes)
    {
        FastRpcItem[] itemArray = CreateItems(items);
        byte[] payload = CreateBytes(dataBytes);
        return new FastRpcBigObject
        {
            Id = 3,
            Items = itemArray,
            Payload = payload,
        };
    }

    private static FastRpcItem[] CreateItems(int count)
    {
        FastRpcItem[] items = new FastRpcItem[count];
        for (int i = 0; i < items.Length; i++)
        {
            items[i] = new FastRpcItem
            {
                Index = (ulong)i,
                Value = (ulong)(i * 3 + 1),
            };
        }

        return items;
    }

    private static byte[] CreateBytes(int length)
    {
        byte[] data = GC.AllocateUninitializedArray<byte>(length);
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)i;
        }

        return data;
    }

    public static byte[] BuildJsonRpcRequest(string method)
    {
        ArrayBufferWriter<byte> buffer = new(128);
        using Utf8JsonWriter writer = new(buffer);

        writer.WriteStartObject();
        writer.WriteString("jsonrpc", "2.0");
        writer.WriteNumber("id", 1);
        writer.WriteString("method", method);
        writer.WriteStartArray("params");
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();

        return buffer.WrittenSpan.ToArray();
    }

    public static byte[] BuildJsonRpcBatchRequest(string method, int count)
    {
        ArrayBufferWriter<byte> buffer = new(512);
        using Utf8JsonWriter writer = new(buffer);

        writer.WriteStartArray();
        for (int i = 0; i < count; i++)
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WriteNumber("id", i + 1);
            writer.WriteString("method", method);
            writer.WriteStartArray("params");
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.Flush();

        return buffer.WrittenSpan.ToArray();
    }
}

[SszContainer]
internal partial struct FastRpcSimpleObject
{
    public ulong Id { get; set; }
    public ulong Count { get; set; }
    public bool Enabled { get; set; }
}

[SszContainer]
internal partial struct FastRpcItem
{
    public ulong Index { get; set; }
    public ulong Value { get; set; }
}

[SszContainer]
internal partial struct FastRpcComplexObject
{
    public ulong Id { get; set; }
    public ulong Score { get; set; }

    [SszList(64)]
    public FastRpcItem[] Items { get; set; }

    [SszList(16 * 1024)]
    public byte[] Payload { get; set; }
}

[SszContainer]
internal partial struct FastRpcBigObject
{
    public ulong Id { get; set; }

    [SszList(1024)]
    public FastRpcItem[] Items { get; set; }

    [SszList(1024 * 1024)]
    public byte[] Payload { get; set; }
}
