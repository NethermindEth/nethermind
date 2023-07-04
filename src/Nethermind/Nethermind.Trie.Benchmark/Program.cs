// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;

namespace Nethermind.Trie.Benchmark
{
    public static class Program
    {
        public static void Main(string[] args)
#if DEBUG
            => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly)
                .Run(args, new DebugInProcessConfig());
#else
        {
            BenchmarkRunner.Run<TreeStoreBenchmark>();
            // BenchmarkRunner.Run<CacheBenchmark>();
            // BenchmarkRunner.Run<TrieNodeBenchmark>();
            Console.ReadLine();
        }
#endif
    }
}
