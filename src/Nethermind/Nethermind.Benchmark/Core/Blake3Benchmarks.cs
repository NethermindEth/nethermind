// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Pbt;

namespace Nethermind.Benchmarks.Core
{
    /// <summary>Managed BLAKE3 against the native binding, at the input sizes EIP-8297 key derivation uses.</summary>
    public class Blake3Benchmarks
    {
        private byte[] _input;
        private readonly byte[] _output = new byte[32];

        [Params(32, 64, 1024, 8192)]
        public int Size { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            _input = new byte[Size];
            new Random(42).NextBytes(_input);
        }

        [Benchmark(Baseline = true)]
        public void Native() => global::Blake3.Hasher.Hash(_input, _output);

        [Benchmark]
        public void Managed() => Blake3Managed.Hash(_input, _output);
    }
}
