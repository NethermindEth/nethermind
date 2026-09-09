// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using BenchmarkDotNet.Attributes;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz;
using Nethermind.Serialization.Ssz.Merkleization;
using SszCodec = Nethermind.Serialization.Ssz.Ssz;

namespace Nethermind.Benchmarks.Ssz;

[MemoryDiagnoser]
public class SszBenchmarks
{
    private SszBenchmarkItem[] _items = null!;
    private byte[] _encoded = null!;
    private BitArray _bits = null!;
    private byte[] _encodedBits = null!;

    [Params(1, 32, 1024)]
    public int Count { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _items = new SszBenchmarkItem[Count];
        for (int i = 0; i < Count; i++) _items[i] = new() { First = (ulong)i, Second = (ulong)i + 1 };
        _encoded = SszBenchmarkItem.Encode(_items);
        _bits = new(_encoded) { Length = _encoded.Length * 8 - 3 };
        _encodedBits = new byte[_encoded.Length];
        SszCodec.Encode(_encodedBits, _bits, _bits.Length);
    }

    [Benchmark]
    public UInt256 MerkleizeItems()
    {
        SszBenchmarkItem.MerkleizeList(_items, 2048, out UInt256 root);
        return root;
    }

    [Benchmark]
    public void EncodeItems() => SszBenchmarkItem.Encode(_encoded, _items);

    [Benchmark]
    public SszBenchmarkItem[] DecodeItems()
    {
        SszBenchmarkItem.Decode(_encoded, out SszBenchmarkItem[] items);
        return items;
    }

    [Benchmark]
    public UInt256 MerkleizeBytes()
    {
        Merkle.Merkleize(out UInt256 root, _encoded);
        return root;
    }

    [Benchmark]
    public void EncodeBits() => SszCodec.Encode(_encodedBits, _bits, _bits.Length);

    [Benchmark]
    public BitArray DecodeBits() => SszCodec.DecodeBitlist(_encodedBits);

    [Benchmark]
    public UInt256 MerkleizeBits()
    {
        Merkle.Merkleize(out UInt256 root, _bits, (ulong)_bits.Length);
        return root;
    }
}

[SszContainer]
public partial struct SszBenchmarkItem
{
    public ulong First { get; set; }
    public ulong Second { get; set; }
}

[MemoryDiagnoser]
public class SszMerkleBenchmarks
{
    private byte[] _bytes = null!;

    [Params(32768, 1048544, 1048575, 1048576, 4194304)]
    public int ByteLength { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _bytes = new byte[ByteLength];
        new Random(42).NextBytes(_bytes);
    }

    [Benchmark]
    public UInt256 MerkleizeBytes()
    {
        Merkle.Merkleize(out UInt256 root, _bytes);
        return root;
    }
}
