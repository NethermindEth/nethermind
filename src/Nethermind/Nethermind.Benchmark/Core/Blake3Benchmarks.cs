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
        private byte[] _secondInput;
        private readonly byte[] _output = new byte[32];
        private readonly byte[] _secondOutput = new byte[32];
        private byte[] _manyInputs;
        private readonly byte[] _manyOutputs = new byte[ManyCount * 32];

        private const int ManyCount = 16;

        [Params(32, 64, 67, 99, 1024, 8192)]
        public int Size { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            _input = new byte[Size];
            new Random(42).NextBytes(_input);
            _secondInput = new byte[Size];
            new Random(43).NextBytes(_secondInput);
            _manyInputs = new byte[ManyCount * Size];
            new Random(44).NextBytes(_manyInputs);
        }

        [Benchmark(Baseline = true)]
        public void Native() => global::Blake3.Hasher.Hash(_input, _output);

        [Benchmark]
        public void Managed() => Blake3Managed.Hash(_input, _output);

        [Benchmark]
        public void ManagedTwice()
        {
            Blake3Managed.Hash(_input, _output);
            Blake3Managed.Hash(_secondInput, _secondOutput);
        }

        [Benchmark]
        public void ManagedTwo() => Blake3Managed.HashTwo(_input, _output, _secondInput, _secondOutput);

        [Benchmark(OperationsPerInvoke = ManyCount)]
        public void ManagedPairsOfSixteen()
        {
            for (int index = 0; index < ManyCount; index += 2)
            {
                Blake3Managed.HashTwo(_manyInputs.AsSpan(index * Size, Size), _manyOutputs.AsSpan(index * 32, 32),
                    _manyInputs.AsSpan((index + 1) * Size, Size), _manyOutputs.AsSpan((index + 1) * 32, 32));
            }
        }

        [Benchmark(OperationsPerInvoke = ManyCount)]
        public void ManagedManyOfSixteen() => Blake3Managed.HashMany(_manyInputs, Size, _manyOutputs);
    }
}
