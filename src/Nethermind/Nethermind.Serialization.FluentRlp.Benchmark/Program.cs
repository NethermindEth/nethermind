// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using Nethermind.Int256;
using Nethermind.Serialization.FluentRlp.Instances;
using CoreAddress = Nethermind.Core.Address;
using CurrentRlp = Nethermind.Serialization.Rlp;

// The namespace deliberately avoids nesting under `Nethermind.Serialization.Rlp`, which would shadow
// FluentRlp's `RlpReader`/`RlpWriter` with the identically-named types in that namespace.
namespace Nethermind.Serialization.FluentRlp.Benchmark;

/// <summary>An EIP-2930 storage key, which is encoded as 32 bytes and not as an integer.</summary>
[RlpFixedLength(32)]
public readonly record struct StorageKey(UInt256 Value);

public static class AccessListRlpConverters
{
    public static CoreAddress ReadAddress(this ref RlpReader reader) => new(reader.ReadBytes(CoreAddress.Size));

    public static void Write(this ref RlpWriter writer, CoreAddress value) => writer.Write(value.Bytes);

    public static StorageKey ReadStorageKey(this ref RlpReader reader) => new(UInt256RlpConverter.ReadFixed(ref reader));

    public static void Write(this ref RlpWriter writer, StorageKey value) => UInt256RlpConverter.WriteFixed(ref writer, value.Value);
}

[RlpSerializable]
public record AccessListItem([RlpFixedLength(CoreAddress.Size)] CoreAddress Address, List<StorageKey> StorageKeys);

[RlpSerializable(RlpRepresentation.Newtype)]
public record AccessList(List<AccessListItem> Items);

/// <summary>Compares FluentRlp with the current decoder on the same EIP-2930 access list and the same bytes.</summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory, BenchmarkLogicalGroupRule.ByParams)]
[CategoriesColumn]
public class CurrentFluentBenchmark
{
    private Nethermind.Core.Eip2930.AccessList _current = null!;
    private AccessList _fluent = null!;
    private byte[] _encoded = null!;
    private byte[] _buffer = null!;
    private ArrayBufferWriter<byte> _bufferWriter = null!;

    [Params(10, 100, 1_000)]
    public int AddressCount { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _current = BuildCurrent(new Random(42));
        _fluent = BuildFluent(new Random(42));
        _encoded = EncodeCurrent();
        _buffer = new byte[_encoded.Length];
        _bufferWriter = new ArrayBufferWriter<byte>(_encoded.Length);

        if (!_encoded.AsSpan().SequenceEqual(EncodeFluent())) throw new InvalidOperationException("Encodings differ");
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Encode")]
    public byte[] EncodeCurrent()
    {
        CurrentRlp.Eip2930.AccessListDecoder decoder = CurrentRlp.Eip2930.AccessListDecoder.Instance;
        int length = decoder.GetLength(_current, CurrentRlp.RlpBehaviors.None);
        byte[] buffer = new byte[length];
        CurrentRlp.RlpWriter writer = new(buffer);
        decoder.Encode(ref writer, _current);
        return buffer;
    }

    [Benchmark]
    [BenchmarkCategory("Encode")]
    public byte[] EncodeFluent() => Rlp.Write<AccessListRlpConverter, AccessList>(_fluent);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("EncodeToBuffer")]
    public int EncodeToBufferCurrent()
    {
        CurrentRlp.Eip2930.AccessListDecoder decoder = CurrentRlp.Eip2930.AccessListDecoder.Instance;
        int length = decoder.GetLength(_current, CurrentRlp.RlpBehaviors.None);
        CurrentRlp.RlpWriter writer = new(_buffer.AsSpan(0, length));
        decoder.Encode(ref writer, _current);
        return length;
    }

    [Benchmark]
    [BenchmarkCategory("EncodeToBuffer")]
    public int EncodeToBufferFluent()
    {
        _bufferWriter.ResetWrittenCount();
        return Rlp.Write<AccessListRlpConverter, AccessList>(_bufferWriter, _fluent);
    }

    [Benchmark]
    [BenchmarkCategory("EncodeToBuffer")]
    public int EncodeToBufferFluentBuffered()
    {
        _bufferWriter.ResetWrittenCount();
        return Rlp.Write(_bufferWriter, _fluent, static (ref RlpWriter writer, AccessList value) => writer.Write(value));
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Decode")]
    public Nethermind.Core.Eip2930.AccessList DecodeCurrent()
        => CurrentRlp.Eip2930.AccessListDecoder.Instance.Decode(_encoded)!;

    [Benchmark]
    [BenchmarkCategory("Decode")]
    public AccessList DecodeFluent()
        => Rlp.Read(_encoded, static (scoped ref RlpReader reader) => reader.ReadAccessList());

    /// <summary>Decodes with FluentRlp to the same type as the current decoder, to compare the readers alone.</summary>
    [Benchmark]
    [BenchmarkCategory("Decode")]
    public Nethermind.Core.Eip2930.AccessList DecodeFluentToCurrentType()
        => Rlp.Read(_encoded, static (scoped ref RlpReader reader) => reader.ReadSequence(static (scoped ref RlpReader r) =>
        {
            Nethermind.Core.Eip2930.AccessList.Builder builder = new();
            while (r.HasNext)
            {
                r.ReadSequence(builder, static (scoped ref RlpReader r, Nethermind.Core.Eip2930.AccessList.Builder builder) =>
                {
                    builder.AddAddress(r.ReadAddress());
                    return r.ReadSequence(builder, static (scoped ref RlpReader r, Nethermind.Core.Eip2930.AccessList.Builder builder) =>
                    {
                        while (r.HasNext)
                        {
                            builder.AddStorage(r.ReadStorageKey().Value);
                        }

                        return builder;
                    });
                });
            }

            return builder.Build();
        }));

    private Nethermind.Core.Eip2930.AccessList BuildCurrent(Random rnd)
    {
        Nethermind.Core.Eip2930.AccessList.Builder builder = new();
        for (int i = 0; i < AddressCount; i++)
        {
            builder.AddAddress(BuildAddress(rnd));
            int keyCount = rnd.Next(10);
            for (int j = 0; j < keyCount; j++)
            {
                builder.AddStorage(BuildStorageKey(rnd));
            }
        }

        return builder.Build();
    }

    private AccessList BuildFluent(Random rnd)
    {
        List<AccessListItem> items = new(AddressCount);
        for (int i = 0; i < AddressCount; i++)
        {
            CoreAddress address = BuildAddress(rnd);
            int keyCount = rnd.Next(10);
            List<StorageKey> keys = new(keyCount);
            for (int j = 0; j < keyCount; j++)
            {
                keys.Add(new StorageKey(BuildStorageKey(rnd)));
            }

            items.Add(new AccessListItem(address, keys));
        }

        return new AccessList(items);
    }

    private static CoreAddress BuildAddress(Random rnd)
    {
        byte[] bytes = new byte[CoreAddress.Size];
        rnd.NextBytes(bytes);
        return new CoreAddress(bytes);
    }

    private static UInt256 BuildStorageKey(Random rnd)
    {
        byte[] bytes = new byte[32];
        rnd.NextBytes(bytes);
        return new UInt256(bytes, isBigEndian: true);
    }
}

public static class Program
{
    public static void Main(string[] args) => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
