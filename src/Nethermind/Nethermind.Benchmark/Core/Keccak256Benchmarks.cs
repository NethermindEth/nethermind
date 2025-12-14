// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;

using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;

using System;

namespace Nethermind.Benchmarks.Core;

[HardwareCounters(HardwareCounter.InstructionRetired, HardwareCounter.TotalCycles, HardwareCounter.BranchMispredictions)]
public class Keccak256Benchmarks
{
    public enum Scenario
    {
        Empty = 0,
        Address,
        UInt256One,
        UInt256MaxValue,
        LargeData
    }

    private byte[] _a;
    private readonly byte[] _output = new byte[32];

    private static readonly byte[][] _scenarios =
    {
        new byte[]{},
        TestItem.AddressA.Bytes,
        UInt256.One.ToBigEndian(),
        UInt256.MaxValue.ToBigEndian(),
        new byte[100000],
    };

    [Params(Scenario.Empty, Scenario.Address, Scenario.UInt256One, Scenario.UInt256MaxValue, Scenario.LargeData)]
    public Scenario ScenarioIndex { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _a = _scenarios[(int)ScenarioIndex];
    }

    //[Benchmark]
    //public void Avx2()
    //{
    //    KeccakHash.BenchmarkHash(_a, _output, KeccakHash.Implementation.Avx2);
    //}

    [Benchmark]
    public void Avx512()
    {
        KeccakHash.BenchmarkHash(_a, _output, KeccakHash.Implementation.Avx512);
    }

    [Benchmark(Baseline = true)]
    public void Scalar()
    {
        KeccakHash.BenchmarkHash(_a, _output, KeccakHash.Implementation.Software);
    }
}
