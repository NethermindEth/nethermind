// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Running;

namespace Nethermind.Abi.Benchmark;

public class Program
{
    public static void Main(string[] args) =>
        BenchmarkRunner.Run(typeof(Program).Assembly, args: args);
}
