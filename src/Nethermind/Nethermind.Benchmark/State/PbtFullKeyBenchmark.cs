// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using Nethermind.Int256;
using Nethermind.Pbt;

namespace Nethermind.Benchmarks.State;

[MemoryDiagnoser]
public class PbtFullKeyBenchmark
{
    private byte[] _bytes;
    private PbtStorageFullKey _key;
    private PbtStorageFullKey _equalKey;
    private PbtStorageFullKey _differentKey;
    private Dictionary<PbtStorageFullKey, int> _dictionary;

    [Params(1, 34, 66)]
    public int ByteLength { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _bytes = new byte[ByteLength];
        new Random(8297).NextBytes(_bytes);
        _key = new PbtStorageFullKey(_bytes);
        _equalKey = new PbtStorageFullKey(_bytes);
        byte[] differentBytes = (byte[])_bytes.Clone();
        differentBytes[^1] ^= 1;
        _differentKey = new PbtStorageFullKey(differentBytes);
        _dictionary = new Dictionary<PbtStorageFullKey, int> { [_key] = 42 };
    }

    [Benchmark]
    public PbtStorageFullKey Construct() => new(_bytes);

    [Benchmark]
    public bool EqualKeys() => _key.Equals(_equalKey);

    [Benchmark]
    public int CompareKeys() => _key.CompareTo(_differentKey);

    [Benchmark]
    public int HashKey() => _key.GetHashCode();

    [Benchmark]
    public int FirstDifferingBit() => _key.FirstDifferingBit(_differentKey);

    [Benchmark]
    public int DictionaryLookup() => _dictionary[_equalKey];
}

[MemoryDiagnoser]
public class PbtFullKeyDerivationBenchmark
{
    private byte[] _address;
    private readonly UInt256 _slot = new(256);

    [GlobalSetup]
    public void Setup()
    {
        _address = new byte[32];
        new Random(8297).NextBytes(_address);
    }

    [Benchmark]
    public PbtFullKey Account() => Eip8297KeyDerivation.AccountKey(_address, 0);

    [Benchmark]
    public PbtStorageFullKey Storage() => Eip8297KeyDerivation.StorageKey(_address, _slot);
}

[MemoryDiagnoser]
[GenericTypeArguments(typeof(PbtFullKey))]
[GenericTypeArguments(typeof(PbtStorageFullKey))]
public class PbtWriteBatchMemoryBenchmark<TKey> where TKey : struct, IPbtKey<TKey>
{
    private TKey[] _keys;
    private PbtWriteBatchBuilder<TKey> _builder;

    [Params(1000)]
    public int Count { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _keys = new TKey[Count];
        byte[] bytes = new byte[34];
        for (int index = 0; index < Count; index++)
        {
            bytes[^2] = (byte)(index >> 8);
            bytes[^1] = (byte)index;
            _keys[index] = TKey.Create(bytes);
        }
        _builder = new(0);
        foreach (TKey key in _keys) _builder.Set(key, default);
        using PbtWriteBatch<TKey> warmup = _builder.Build();
    }

    [GlobalCleanup]
    public void Cleanup() => _builder.Dispose();

    [Benchmark]
    public int PopulateColdBuilder()
    {
        using PbtWriteBatchBuilder<TKey> builder = new(0);
        foreach (TKey key in _keys) builder.Set(key, default);
        return builder.Count;
    }

    [Benchmark]
    public int BuildWarmBatch()
    {
        using PbtWriteBatch<TKey> batch = _builder.Build();
        return batch.Count;
    }
}
