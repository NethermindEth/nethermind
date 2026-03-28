// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;

namespace Nethermind.Benchmarks;

public static class Program
{
    public static void Main(string[] args)
#if DEBUG
        => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly)
            .Run(args, new DebugInProcessConfig());
#else
        => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
#endif
}
