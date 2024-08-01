// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Nethermind.Core.Extensions;
using Nethermind.Crypto.Blake2;

namespace Nethermind.Benchmarks.Evm
{
    public class Blake2Benchmark
    {
        private Blake2Compression _blake2Compression = new Blake2Compression();

        private byte[] input = Bytes.FromHexString("0000000148c9bdf267e6096a3ba7ca8485ae67bb2bf894fe72f36e3cf1361d5f3af54fa5d182e6ad7f520e511f6c3e2b8c68059b6bbd41fbabd9831f79217e1319cde05b61626300000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000300000000000000000000000000000001");

        [GlobalSetup]
        public void Setup()
        {
            if (!Bytes.AreEqual(Current(), Improved()))
            {
                throw new InvalidBenchmarkDeclarationException("blakes");
            }
        }

        [Benchmark(Baseline = true)]
        public Span<byte> Current()
        {
            Span<byte> result = new byte[64];
            _blake2Compression.Compress(input, result);
            return result;
        }

        [Benchmark]
        public Span<byte> Improved()
        {
            Span<byte> result = new byte[64];
            _blake2Compression.Compress(input, result);
            return result;
        }
    }
}
