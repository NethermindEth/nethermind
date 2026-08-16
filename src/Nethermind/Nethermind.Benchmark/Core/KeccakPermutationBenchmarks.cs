// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.Intrinsics.Arm;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Filters;
using Nethermind.Core.Crypto;

namespace Nethermind.Benchmarks.Core;

[Config(typeof(ArmSha3Config))]
public class KeccakPermutationBenchmarks
{
    private sealed class ArmSha3Config : ManualConfig
    {
        public ArmSha3Config() => AddFilter(new SimpleFilter(
            benchmarkCase => benchmarkCase.Descriptor.WorkloadMethod.Name != nameof(ArmSha3) || Sha3.IsSupported));
    }

    private readonly ulong[] _scalarState = CreateState();
    private readonly ulong[] _armSha3State = CreateState();

    [GlobalSetup]
    public void Setup()
    {
        if (!Sha3.IsSupported)
        {
            return;
        }

        ulong[] scalarState = CreateState();
        ulong[] armSha3State = CreateState();
        KeccakHash.KeccakF1600Scalar(scalarState);
        KeccakHash.KeccakF1600ArmSha3(armSha3State);

        if (!scalarState.AsSpan().SequenceEqual(armSha3State))
        {
            throw new InvalidOperationException("Arm SHA-3 permutation does not match the scalar implementation.");
        }
    }

    [Benchmark(Baseline = true)]
    public ulong Scalar()
    {
        KeccakHash.KeccakF1600Scalar(_scalarState);
        return _scalarState[0];
    }

    [Benchmark]
    public ulong ArmSha3()
    {
        KeccakHash.KeccakF1600ArmSha3(_armSha3State);
        return _armSha3State[0];
    }

    private static ulong[] CreateState()
    {
        ulong[] state = new ulong[25];
        for (int lane = 0; lane < state.Length; lane++)
        {
            state[lane] = unchecked((ulong)(lane + 1) * 0x9e3779b97f4a7c15UL);
        }

        return state;
    }
}
