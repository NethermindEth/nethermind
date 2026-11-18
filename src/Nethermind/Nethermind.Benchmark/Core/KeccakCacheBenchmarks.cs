// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Crypto;

namespace Nethermind.Benchmarks.Core
{
    public class KeccakCacheBenchmarks
    {
        private const int MissInputs = 1 << 20;

        private byte[] _hit = [];
        private byte[] _misses = [];
        private int _next;

        [Params(20, 32, 64, 88, 89)]
        public int Length { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            Random random = new(42);
            _hit = new byte[Length];
            random.NextBytes(_hit);
            _misses = new byte[MissInputs * Length];
            random.NextBytes(_misses);
            KeccakCache.Compute(_hit);
        }

        [Benchmark(Baseline = true)]
        public ValueHash256 Uncached() => ValueKeccak.Compute(_hit);

        [Benchmark]
        public ValueHash256 Hit() => KeccakCache.Compute(_hit);

        [Benchmark]
        public ValueHash256 Miss()
        {
            int index = _next;
            _next = (index + 1) & (MissInputs - 1);
            return KeccakCache.Compute(_misses.AsSpan(index * Length, Length));
        }
    }
}
