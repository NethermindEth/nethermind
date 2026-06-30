// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Crypto;

namespace Nethermind.Benchmarks.Core
{
    /// <summary>
    /// Compares hashing <c>Vector&lt;ulong&gt;.Count</c> independent inputs via the scalar
    /// <see cref="KeccakHash"/> N times versus one <see cref="KeccakHashBatch"/> call. The batched path
    /// only wins on wide vectors with a native vector rotate (AVX-512); run on x86 with and without
    /// <c>DOTNET_MaxVectorTBitwidth=512</c> to see lanes=4 (AVX2) vs lanes=8 (AVX-512).
    /// </summary>
    public class KeccakBatchBenchmarks
    {
        private byte[] _inputs = [];
        private byte[] _outputs = [];
        private int _lanes;

        // Representative trie-node RLP sizes: leaf/value, a one-rate-block node, a full 16-child branch.
        [Params(32, 136, 544)]
        public int InputLength { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            _lanes = Vector<ulong>.Count;
            _inputs = new byte[_lanes * InputLength];
            new Random(42).NextBytes(_inputs);
            _outputs = new byte[_lanes * 32];
        }

        [Benchmark(Baseline = true)]
        public void Scalar()
        {
            for (int k = 0; k < _lanes; k++)
            {
                KeccakHash.ComputeHash(_inputs.AsSpan(k * InputLength, InputLength), _outputs.AsSpan(k * 32, 32));
            }
        }

        [Benchmark]
        public void Batched() => KeccakHashBatch.ComputeHash256(_inputs, InputLength, _outputs);
    }
}
