// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Crypto;
using Org.BouncyCastle.Crypto.Digests;

namespace Nethermind.Precompiles.Benchmark
{
    /// <summary>
    /// Confirms the per-call allocation reduction from reusing a thread-static RIPEMD-160 digest
    /// (RIPEMD-160 is the only pure-managed-compute precompile) instead of allocating a fresh digest
    /// — together with its internal working buffers — on every call. Compare the <c>Allocated</c>
    /// column of the two benchmarks.
    /// </summary>
    [MemoryDiagnoser]
    public class Ripemd160AllocationBenchmark
    {
        private const int HashOutputLength = 32;

        private byte[] _input = null!;

        [Params(128)]
        public int InputSize { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            _input = new byte[InputSize];
            for (int i = 0; i < _input.Length; i++)
            {
                _input[i] = (byte)i;
            }
        }

        // Pre-PR behaviour: a fresh digest (plus its internal xBuf/X working buffers) is allocated per call.
        [Benchmark(Baseline = true)]
        public byte[] FreshDigestPerCall()
        {
            RipeMD160Digest digest = new();
            digest.BlockUpdate(_input);
            byte[] result = new byte[HashOutputLength];
            int length = digest.GetDigestSize();
            digest.DoFinal(result.AsSpan(HashOutputLength - length, length));
            return result;
        }

        // Optimized: reuses a single thread-static digest; only the 32-byte output array is allocated per call.
        [Benchmark]
        public byte[] ReusedThreadStaticDigest() => Ripemd.Compute(_input);
    }
}
