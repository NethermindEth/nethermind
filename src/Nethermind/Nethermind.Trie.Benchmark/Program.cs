// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;

namespace Nethermind.Trie.Benchmark
{
    public static class Program
    {
#if DEBUG
        public static void Main(string[] args) => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly)
            .Run(args, new DebugInProcessConfig());
#else
        public static void Main(string[] args) => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
#endif
    }
}
