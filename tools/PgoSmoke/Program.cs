// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

IOperation operation = args.Length > 0 && args[0] == "cold" ? new Subtract() : new Add();
Stopwatch timer = Stopwatch.StartNew();
long iterations = 0;
do
{
    for (int i = 0; i < 100_000; i++)
    {
        int actual = Workload.Run(operation, i);
        int expected = operation is Add ? i + 17 : i - 17;
        if (actual != expected) throw new InvalidOperationException($"Result mismatch at {i}: {actual} != {expected}");
    }
    iterations += 100_000;
    Thread.Sleep(10);
} while (timer.Elapsed < TimeSpan.FromSeconds(5));
Console.WriteLine($"PASS: {iterations} results; dynamic code supported: {RuntimeFeature.IsDynamicCodeSupported}");

interface IOperation
{
    int Apply(int value);
}

sealed class Add : IOperation
{
    public int Apply(int value) => value + 17;
}

sealed class Subtract : IOperation
{
    public int Apply(int value) => value - 17;
}

static class Workload
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Run(IOperation operation, int value) => operation.Apply(value);
}
