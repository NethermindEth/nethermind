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
    private PbtFullKey _key;
    private PbtFullKey _equalKey;
    private PbtFullKey _differentKey;
    private Dictionary<PbtFullKey, int> _dictionary;

    [Params(1, 34, 66)]
    public int ByteLength { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _bytes = new byte[ByteLength];
        new Random(8297).NextBytes(_bytes);
        _key = new PbtFullKey(_bytes);
        _equalKey = new PbtFullKey(_bytes);
        byte[] differentBytes = (byte[])_bytes.Clone();
        differentBytes[^1] ^= 1;
        _differentKey = new PbtFullKey(differentBytes);
        _dictionary = new Dictionary<PbtFullKey, int> { [_key] = 42 };
    }

    [Benchmark]
    public PbtFullKey Construct() => new(_bytes);

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
    public PbtFullKey Storage() => Eip8297KeyDerivation.StorageKey(_address, _slot);
}
