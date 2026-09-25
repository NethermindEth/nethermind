// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using BenchmarkDotNet.Attributes;

namespace Nethermind.Benchmarks.Core;

/// <summary>
/// A <see cref="Vector256{T}"/> field gives its struct 32-byte alignment unless the struct is packed, as
/// <c>ValueHash256</c> now is. Compares a <c>TreePath</c>-shaped key (a hash and a length) in both layouts:
/// 64 bytes padded, 40 bytes packed.
/// </summary>
[ShortRunJob]
[MemoryDiagnoser]
public class Vector256FieldPackingBenchmarks
{
    private const int Count = 16 * 1024;

    private PaddedKey[] _padded;
    private PackedKey[] _packed;
    private Dictionary<PaddedKey, int> _paddedMap;
    private Dictionary<PackedKey, int> _packedMap;

    [GlobalSetup]
    public void Setup()
    {
        Random random = new(42);
        byte[] bytes = new byte[32];
        _padded = new PaddedKey[Count];
        _packed = new PackedKey[Count];
        for (int i = 0; i < Count; i++)
        {
            random.NextBytes(bytes);
            Vector256<byte> value = Vector256.Create(bytes);
            _padded[i] = new PaddedKey(new PaddedHash(value), i & 63);
            _packed[i] = new PackedKey(new PackedHash(value), i & 63);
        }

        _paddedMap = new Dictionary<PaddedKey, int>(Count);
        _packedMap = new Dictionary<PackedKey, int>(Count);
        for (int i = 0; i < Count; i++)
        {
            _paddedMap[_padded[i]] = i;
            _packedMap[_packed[i]] = i;
        }
    }

    [Benchmark(Baseline = true)]
    public int Padded_DictionaryInsert()
    {
        Dictionary<PaddedKey, int> map = new(Count);
        foreach (PaddedKey key in _padded) map[key] = 0;
        return map.Count;
    }

    [Benchmark]
    public int Packed_DictionaryInsert()
    {
        Dictionary<PackedKey, int> map = new(Count);
        foreach (PackedKey key in _packed) map[key] = 0;
        return map.Count;
    }

    [Benchmark]
    public int Padded_DictionaryLookup()
    {
        int hits = 0;
        foreach (PaddedKey key in _padded) hits += _paddedMap.ContainsKey(key) ? 1 : 0;
        return hits;
    }

    [Benchmark]
    public int Packed_DictionaryLookup()
    {
        int hits = 0;
        foreach (PackedKey key in _packed) hits += _packedMap.ContainsKey(key) ? 1 : 0;
        return hits;
    }

    [Benchmark]
    public int Padded_EqualityScan()
    {
        PaddedKey needle = _padded[^1];
        int matches = 0;
        foreach (PaddedKey key in _padded) matches += key.Equals(needle) ? 1 : 0;
        return matches;
    }

    [Benchmark]
    public int Packed_EqualityScan()
    {
        PackedKey needle = _packed[^1];
        int matches = 0;
        foreach (PackedKey key in _packed) matches += key.Equals(needle) ? 1 : 0;
        return matches;
    }

    private readonly struct PaddedHash(Vector256<byte> value) : IEquatable<PaddedHash>
    {
        private readonly Vector256<byte> _value = value;
        public bool Equals(PaddedHash other) => _value == other._value;
        public override bool Equals(object obj) => obj is PaddedHash other && Equals(other);
        public override int GetHashCode() => (int)_value.AsUInt64().GetElement(0);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private readonly struct PackedHash(Vector256<byte> value) : IEquatable<PackedHash>
    {
        private readonly Vector256<byte> _value = value;
        public bool Equals(PackedHash other) => _value == other._value;
        public override bool Equals(object obj) => obj is PackedHash other && Equals(other);
        public override int GetHashCode() => (int)_value.AsUInt64().GetElement(0);
    }

    private readonly struct PaddedKey(PaddedHash hash, int length) : IEquatable<PaddedKey>
    {
        private readonly PaddedHash _hash = hash;
        private readonly int _length = length;
        public bool Equals(PaddedKey other) => _length == other._length && _hash.Equals(other._hash);
        public override bool Equals(object obj) => obj is PaddedKey other && Equals(other);
        public override int GetHashCode() => _hash.GetHashCode() ^ _length;
    }

    private readonly struct PackedKey(PackedHash hash, int length) : IEquatable<PackedKey>
    {
        private readonly PackedHash _hash = hash;
        private readonly int _length = length;
        public bool Equals(PackedKey other) => _length == other._length && _hash.Equals(other._hash);
        public override bool Equals(object obj) => obj is PackedKey other && Equals(other);
        public override int GetHashCode() => _hash.GetHashCode() ^ _length;
    }
}
