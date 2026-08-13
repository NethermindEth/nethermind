// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;
using Nethermind.Core.Crypto;

namespace Nethermind.Precompiles.Benchmark
{
    public class KeccakBenchmark
    {
        public readonly struct Param
        {
            private static readonly Random _random = new(42);

            public Param(byte[] bytes)
            {
                Bytes = bytes;
                _random.NextBytes(Bytes);
            }

            public byte[] Bytes { get; }

            public override string ToString() => $"bytes[{Bytes.Length.ToString().PadLeft(4, '0')}]";
        }

        public IEnumerable<Param> Inputs
        {
            get
            {
                for (int i = 0; i <= 512; i += 4)
                {
                    yield return new Param(new byte[i]);
                }
            }
        }

        [ParamsSource(nameof(Inputs))]
        public Param Input { get; set; }

        [Benchmark(Baseline = true)]
        public Span<byte> Baseline() => ValueKeccak.Compute(Input.Bytes).BytesAsSpan;
    }

    [Config(typeof(InProcessConfig))]
    [MemoryDiagnoser]
    public class KeccakPermutationBenchmark
    {
        private sealed class InProcessConfig : ManualConfig
        {
            public InProcessConfig()
            {
                WithUnionRule(ConfigUnionRule.AlwaysUseLocal);
                AddJob(Job.MediumRun.WithToolchain(InProcessNoEmitToolchain.Instance));
            }
        }

        private const int LaneCount = 25;
        private const ulong InitialLaneMultiplier = 0x9E3779B97F4A7C15UL;
        private const ulong InitialLaneXor = 0xD1B54A32D192ED03UL;

        private readonly ulong[] _scalarState = new ulong[LaneCount];
        private readonly ulong[] _sveState = new ulong[LaneCount];

        [GlobalSetup]
        public void Setup()
        {
            if (!KeccakHash.IsSve2KeccakSupported())
                throw new PlatformNotSupportedException("KeccakPermutationBenchmark requires SVE2 SHA3 support.");

            for (int lane = 0; lane < LaneCount; lane++)
            {
                _scalarState[lane] = (ulong)(lane + 1) * InitialLaneMultiplier ^ InitialLaneXor;
            }

            _scalarState.AsSpan().CopyTo(_sveState);
            KeccakHash.KeccakF1600Scalar(_scalarState);
            KeccakHash.KeccakF1600Sve2(_sveState);

            if (!_scalarState.AsSpan().SequenceEqual(_sveState))
                throw new InvalidOperationException("SVE2 Keccak does not match the scalar permutation.");
        }

        [Benchmark(Baseline = true)]
        public void Scalar() => KeccakHash.KeccakF1600Scalar(_scalarState);

        [Benchmark]
        public void Sve2() => KeccakHash.KeccakF1600Sve2(_sveState);
    }
}
