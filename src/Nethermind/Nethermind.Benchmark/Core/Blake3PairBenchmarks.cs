// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Pbt;

namespace Nethermind.Benchmarks.Core
{
    /// <summary>
    /// The EIP-8297 node hash: 64 bytes, frequently with one 32-byte half all zeroes (an empty subtree).
    /// </summary>
    public class Blake3PairBenchmarks
    {
        private readonly byte[] _pair = new byte[64];
        private readonly byte[] _output = new byte[32];

        /// <summary>Which half of the pair is zeroed: neither, the low 32 bytes, or the high 32 bytes.</summary>
        [Params("none", "low", "high")]
        public string ZeroHalf { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            new Random(42).NextBytes(_pair);
            if (ZeroHalf == "low") _pair.AsSpan(0, 32).Clear();
            if (ZeroHalf == "high") _pair.AsSpan(32, 32).Clear();
        }

        [Benchmark(Baseline = true)]
        public void Native() => global::Blake3.Hasher.Hash(_pair, _output);

        [Benchmark]
        public void Managed() => Blake3Managed.Hash(_pair, _output);

        [Benchmark]
        public void ManagedPair() => Blake3Managed.HashPair(_pair.AsSpan(0, 32), _pair.AsSpan(32, 32), _output);
    }
}
