// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Pbt;

namespace Nethermind.Benchmarks.State;

[MemoryDiagnoser]
public class PbtNodePathBenchmark
{
    private byte[] _bytes;
    private PbtStorageFullKey _key;
    private PbtStorageNodePath _path;

    [Params(0, 1, 8, 64, 272, 524, 528)]
    public int BitDepth { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        byte[] keyBytes = new byte[66];
        new Random(8297).NextBytes(keyBytes);
        _key = new PbtStorageFullKey(keyBytes);
        _path = PbtStorageNodePath.FromKey(_key, BitDepth);
        _bytes = _path.Path.ToArray();
    }

    [Benchmark]
    public PbtStorageNodePath Construct() => new(_bytes, BitDepth);

    [Benchmark]
    public PbtStorageNodePath FromKey() => PbtStorageNodePath.FromKey(_key, BitDepth);

    [Benchmark]
    public byte[] Encode() => _path.Encode();

    [Benchmark]
    public IPbtNodePath LocateAndReconstruct()
    {
        PbtNodeGroupLocation location = PbtFourLevelGroupGeometry.Locate(_path);
        return PbtFourLevelGroupGeometry.Reconstruct(location.GroupKey, location.Position);
    }
}

[MemoryDiagnoser]
public class PbtNodePathAppendBenchmark
{
    private PbtStorageNodePath _path;
    private PbtBitPrefix _prefix;

    [Params(1, 8, 272, 528)]
    public int ResultBitDepth { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        byte[] keyBytes = new byte[66];
        new Random(8297).NextBytes(keyBytes);
        PbtStorageFullKey key = new(keyBytes);
        int pathDepth = Math.Min(7, ResultBitDepth - 1);
        _path = PbtStorageNodePath.FromKey(key, pathDepth);
        _prefix = PbtBitPrefix.FromKey(key, pathDepth, ResultBitDepth - pathDepth - 1);
    }

    [Benchmark]
    public PbtStorageNodePath Append() => _path.Append(_prefix, 1);
}

[MemoryDiagnoser]
[GenericTypeArguments(typeof(PbtNodePath))]
[GenericTypeArguments(typeof(PbtStorageNodePath))]
public class PbtNodePathMemoryBenchmark<TPath> where TPath : struct, IPbtNodePath<TPath>
{
    private byte[] _bytes;

    [Params(0, 8, 272)]
    public int BitDepth { get; set; }

    [GlobalSetup]
    public void Setup() => _bytes = new byte[(BitDepth + 7) / 8];

    [Benchmark]
    public TPath Construct() => TPath.Create(_bytes, BitDepth);
}
