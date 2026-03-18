// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Threading.Tasks;
using BenchmarkDotNet.Running;

namespace Nethermind.Evm.Benchmark;

public static class Program
{
    public static async Task Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--newpayload")
        {
            await NewPayloadBenchmark.Run(args.Skip(1).ToArray());
            return;
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
