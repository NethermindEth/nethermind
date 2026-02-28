// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Running;

namespace Nethermind.Evm.Benchmark;

public static class Program
{
    public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--profile")
        {
            ProfileRunner.Run(args);
            return;
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
