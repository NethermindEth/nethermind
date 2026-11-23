// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using Nethermind.Db.LogIndex;

namespace Nethermind.Benchmarks.Store;

/// <summary>
/// Compresses and decompresses one stored log index value: ascending block numbers with small gaps.
/// </summary>
[MemoryDiagnoser]
public class LogIndexCompressionBenchmark
{
    private static readonly byte[] Key = new byte[24];

    private LogIndexStorage.CompressionAlgorithm _algorithm = null!;
    private byte[] _blockNumbers = null!;
    private byte[] _compressed = null!;
    private int[] _decompressed = null!;

    [Params(128, 1_024, 8_192)]
    public int Count { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _algorithm = LogIndexStorage.CompressionAlgorithm.Best.Value;
        Random random = new(42);
        int[] blocks = new int[Count];
        int block = 20_000_000;
        for (int i = 0; i < blocks.Length; i++)
        {
            block += 1 + random.Next(64);
            blocks[i] = block;
        }

        _blockNumbers = MemoryMarshal.AsBytes(blocks.AsSpan()).ToArray();
        _compressed = LogIndexStorage.CompressDbValue(_algorithm, Key, _blockNumbers);
        _decompressed = new int[Count + 16];
    }

    [Benchmark]
    public int Compress() => LogIndexStorage.CompressDbValue(_algorithm, Key, _blockNumbers).Length;

    [Benchmark]
    public void Decompress() => LogIndexStorage.DecompressDbValue(_algorithm, _compressed, _decompressed);
}
