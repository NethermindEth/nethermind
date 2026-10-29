// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Collections;
using NUnit.Framework;

namespace Nethermind.Core.Test.Collections;

public class LoserTreeTests
{
    [Test]
    public void Merges_runs_in_order_keeping_equal_heads_in_run_order([Values(0, 1, 2, 3, 5, 8, 17)] int runCount)
    {
        Random random = new(runCount);
        int[][] runs = new int[runCount][];
        for (int run = 0; run < runCount; run++)
        {
            runs[run] = Enumerable.Range(0, random.Next(0, 12)).Select(_ => random.Next(0, 8)).Order().ToArray();
        }

        IntRuns source = new(runs);
        LoserTree<IntRuns> tree = new(source, new int[runCount]);
        List<(int Value, int Run)> merged = [];
        while (tree.TryPeek(out int run))
        {
            merged.Add((source.Head(run), run));
            tree.Pop();
        }

        List<(int Value, int Run)> expected = runs.SelectMany((values, run) => values.Select(value => (value, run))).OrderBy(head => head.value).ThenBy(head => head.run).ToList();
        Assert.That(merged, Is.EqualTo(expected));
    }

    private sealed class IntRuns(int[][] runs) : ILoserTreeRuns
    {
        private readonly int[] _positions = new int[runs.Length];

        public int Count => runs.Length;
        public int Head(int run) => runs[run][_positions[run]];
        public bool IsExhausted(int run) => _positions[run] >= runs[run].Length;
        public int CompareHeads(int left, int right) => Head(left).CompareTo(Head(right));
        public void Advance(int run) => _positions[run]++;
    }
}
