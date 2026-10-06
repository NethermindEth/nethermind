// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using BenchmarkDotNet.Attributes;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Serialization.Json;
using Nethermind.Specs;

namespace Nethermind.JsonRpc.Benchmark;

/// <summary>Attributes the cost of writing a full block's transactions to each layer between the array and the generated writer.</summary>
[MemoryDiagnoser]
public class BlockTransactionsDispatchBenchmarks
{
    private readonly ArrayBufferWriter<byte> _buffer = new(1 << 20);
    private readonly RpcResultSerializationBenchmarks _rpc = new();
    private JsonSerializerOptions _options = null!;
    private JsonSerializerOptions _registered = null!;
    private BlockTransactions _transactions = null!;
    private TransactionForRpc[] _full = null!;
    private object[] _boxed = null!;
    private IGeneratedJsonWriter _writer = null!;
    private JsonConverter<TransactionForRpc> _dispatch = null!;

    [GlobalSetup]
    public void Setup()
    {
        _rpc.Setup();
        _options = EthereumJsonSerializer.JsonOptions;
        _transactions = new BlockForRpc(_rpc.Block, includeFullTransactionData: true, MainnetSpecProvider.Instance).Transactions!;
        _full = _transactions.Full!;
        _boxed = [.. _full];
        GeneratedJsonWriters.TryGetDispatchWriter(typeof(EIP1559TransactionForRpc), out IGeneratedJsonWriter? writer);
        _writer = writer!;
        _dispatch = (JsonConverter<TransactionForRpc>)TypeInfoJsonSerializer.GetTypeInfo<TransactionForRpc>(_options).Converter;

        // As 7c0ddaac69 wired it: the writer on the options, reached by STJ's dispatch of object[] elements.
        _registered = new JsonSerializerOptions(_options);
        _registered.Converters.Insert(0, (JsonConverter)_writer);

        byte[] expected = Bytes(Wrapper);
        foreach (Func<int> variant in new Func<int>[] { DispatchConverter, RegistryLookup, CachedWriter, RegisteredObjectArray })
        {
            if (!expected.AsSpan().SequenceEqual(Bytes(variant))) throw new InvalidOperationException($"{variant.Method.Name} writes different bytes");
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _rpc.Cleanup();

    [Benchmark(Baseline = true, Description = "BlockTransactionsConverter (head)")]
    public int Wrapper() => Write(static (w, s) => TypeInfoJsonSerializer.Serialize(w, s._transactions, s._options));

    [Benchmark(Description = "TransactionJsonConverter.Write per tx")]
    public int DispatchConverter() => Write(static (w, s) =>
    {
        w.WriteStartArray();
        foreach (TransactionForRpc tx in s._full) s._dispatch.Write(w, tx, s._options);
        w.WriteEndArray();
    });

    [Benchmark(Description = "registry lookup + WriteValue per tx")]
    public int RegistryLookup() => Write(static (w, s) =>
    {
        w.WriteStartArray();
        foreach (TransactionForRpc tx in s._full)
        {
            GeneratedJsonWriters.TryGetDispatchWriter(tx.GetType(), out IGeneratedJsonWriter? writer);
            writer!.WriteValue(w, tx, s._options);
        }

        w.WriteEndArray();
    });

    [Benchmark(Description = "cached writer, WriteValue per tx")]
    public int CachedWriter() => Write(static (w, s) =>
    {
        w.WriteStartArray();
        foreach (TransactionForRpc tx in s._full) s._writer.WriteValue(w, tx, s._options);
        w.WriteEndArray();
    });

    [Benchmark(Description = "object[] with writer on options (7c0ddaac69)")]
    public int RegisteredObjectArray() => Write(static (w, s) => TypeInfoJsonSerializer.Serialize(w, s._boxed, s._registered));

    private int Write(Action<Utf8JsonWriter, BlockTransactionsDispatchBenchmarks> write)
    {
        _buffer.ResetWrittenCount();
        using (Utf8JsonWriter writer = new(_buffer, new JsonWriterOptions { SkipValidation = true, Encoder = _options.Encoder }))
        {
            write(writer, this);
        }

        return _buffer.WrittenCount;
    }

    private byte[] Bytes(Func<int> variant)
    {
        variant();
        return _buffer.WrittenSpan.ToArray();
    }
}
