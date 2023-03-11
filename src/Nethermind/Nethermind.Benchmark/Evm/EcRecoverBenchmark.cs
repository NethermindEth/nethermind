// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace Nethermind.Benchmarks.Evm
{
    public class EcRecoverBenchmark
    {
        [GlobalSetup]
        public void Setup()
        {
        }

        [Benchmark]
        public bool Improved()
        {
            throw new NotImplementedException();
        }

        [Benchmark]
        public bool Current()
        {
            throw new NotImplementedException();
        }
    }
}
