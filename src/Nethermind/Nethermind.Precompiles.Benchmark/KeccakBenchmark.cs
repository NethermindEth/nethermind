// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Crypto;

namespace Nethermind.Precompiles.Benchmark
{
    [MemoryDiagnoser]
    public class KeccakBenchmark
    {
        private readonly byte[] _output = new byte[32];

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

        [Benchmark]
        public void ComputeHash() => KeccakHash.ComputeHash(Input.Bytes, _output);
    }
}
