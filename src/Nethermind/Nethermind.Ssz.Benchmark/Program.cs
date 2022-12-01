// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;

namespace Nethermind.Ssz.Benchmarks
{
    public static class Program
    {
        public static void Main(string[] args)
#if DEBUG
        {
            BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, new DebugInProcessConfig());
            Console.ReadLine();
        }
#else
        {
            // BenchmarkRunner.Run<SszUIntBenchmarks>();
            // BenchmarkRunner.Run<SszBoolBenchmarks>();
            // BenchmarkRunner.Run<SszBeaconBlockHeaderBenchmark>();
            // BenchmarkRunner.Run<SszBeaconBlockBodyBenchmark>();
            BenchmarkRunner.Run<GetProofBenchmarks>();
            Console.ReadLine();
        }
#endif
    }
}
