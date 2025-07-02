// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using Nethermind.Core.Threading;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Benchmarks.Core;

[HideColumns("Job", "RatioSD")]
public class ParallelBenchmark
{
    private int[] _times;

    [GlobalSetup]
    public void Setup()
    {
        _times = new int[200];

        for (int i = 0; i < _times.Length; i++)
        {
            _times[i] = i % 100;
        }
    }

    [Benchmark(Baseline = true)]
    public void ParallelFor()
    {
        Parallel.For(
            0,
            _times.Length,
            (i) => Thread.Sleep(_times[i]));
    }

    [Benchmark]
    public void ParallelForEach()
    {
        Parallel.ForEach(
            _times,
            (time) => Thread.Sleep(time));
    }

    [Benchmark]
    public void UnbalancedParallel()
    {
        ParallelUnbalancedWork.For<int[]>(
            0,
            _times.Length,
            ParallelUnbalancedWork.DefaultOptions,
            _times,
            (i, value) =>
            {
                Thread.Sleep(value[i]);
                return value;
            },
            (value) => { });
    }
}
