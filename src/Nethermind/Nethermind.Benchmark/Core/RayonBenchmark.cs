// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Threading;

namespace Nethermind.Benchmarks.Core;

/// <summary>
/// Compares fork-join over a skewed tree (the BulkSet shape: 16-way fan-out with uneven subtrees) between
/// a single top-level fan-out, as BulkSet does today, and recursive <see cref="Rayon.Join{TSa,TSb,TA,TB}"/>
/// at every node; plus a flat CPU-bound loop across the three parallel-for primitives.
/// </summary>
[MemoryDiagnoser]
[HideColumns("Job", "RatioSD")]
public class RayonBenchmark
{
    private const int MaxChildren = 16;
    private const int WorkPerLeaf = 2_000;

    private sealed class Node(int work, Node[] children)
    {
        public int Work => work;
        public Node[] Children => children;
    }

    private Node _root;
    private int[] _flatWork;

    [Params(1_000, 100_000)]
    public int Leaves { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        Random rng = new(42);
        _root = Build(rng, Leaves, depth: 0);
        _flatWork = new int[Leaves];
        for (int i = 0; i < Leaves; i++)
        {
            _flatWork[i] = rng.Next(WorkPerLeaf / 10, WorkPerLeaf * 2);
        }
    }

    // A skewed tree: each node hands a random share of its leaves to each child, so subtree sizes vary widely.
    private static Node Build(Random rng, int leaves, int depth)
    {
        if (leaves <= 1 || depth >= 8)
        {
            return new Node(rng.Next(WorkPerLeaf / 10, WorkPerLeaf * 2) * Math.Max(leaves, 1), []);
        }

        int childCount = depth == 0 ? MaxChildren : rng.Next(2, MaxChildren + 1);
        Node[] children = new Node[childCount];
        int remaining = leaves;
        for (int i = 0; i < childCount; i++)
        {
            int share = i == childCount - 1 ? remaining : rng.Next(0, remaining + 1) / 2;
            remaining -= share;
            children[i] = Build(rng, share, depth + 1);
        }

        return new Node(rng.Next(WorkPerLeaf / 10, WorkPerLeaf), children);
    }

    private static long Spin(int iterations)
    {
        long acc = iterations;
        for (int i = 0; i < iterations; i++)
        {
            acc = acc * 6364136223846793005L + 1442695040888963407L;
        }

        return acc;
    }

    private static long Sequential(Node node)
    {
        long acc = Spin(node.Work);
        foreach (Node child in node.Children)
        {
            acc += Sequential(child);
        }

        return acc;
    }

    private static long JoinRecursive(Node node)
    {
        long acc = Spin(node.Work);
        return acc + JoinChildren(node.Children, 0, node.Children.Length);
    }

    private static long JoinChildren(Node[] children, int from, int to)
    {
        int count = to - from;
        if (count == 0) return 0;
        if (count == 1) return JoinRecursive(children[from]);
        int mid = from + count / 2;
        (long left, long right) = Rayon.Join(
            (children, from, mid), static s => JoinChildren(s.children, s.from, s.mid),
            (children, mid, to), static s => JoinChildren(s.children, s.mid, s.to));
        return left + right;
    }

    [Benchmark(Baseline = true)]
    public long TreeSequential() => Sequential(_root);

    [Benchmark]
    public long TreeParallelForTopLevel()
    {
        long[] results = new long[_root.Children.Length];
        Parallel.For(0, _root.Children.Length, ParallelUnbalancedWork.DefaultOptions, i => results[i] = Sequential(_root.Children[i]));
        long acc = Spin(_root.Work);
        foreach (long r in results) acc += r;
        return acc;
    }

    [Benchmark]
    public long TreeUnbalancedWorkTopLevel()
    {
        long[] results = new long[_root.Children.Length];
        ParallelUnbalancedWork.For(0, _root.Children.Length, (_root, results), static (i, s) =>
        {
            s.results[i] = Sequential(s._root.Children[i]);
            return s;
        });
        long acc = Spin(_root.Work);
        foreach (long r in results) acc += r;
        return acc;
    }

    [Benchmark]
    public long TreeRayonJoinRecursive() => JoinRecursive(_root);

    [Benchmark]
    public long FlatParallelFor()
    {
        long[] results = new long[_flatWork.Length];
        Parallel.For(0, _flatWork.Length, ParallelUnbalancedWork.DefaultOptions, i => results[i] = Spin(_flatWork[i]));
        return results[0];
    }

    [Benchmark]
    public long FlatUnbalancedWork()
    {
        long[] results = new long[_flatWork.Length];
        ParallelUnbalancedWork.For(0, _flatWork.Length, (_flatWork, results), static (i, s) =>
        {
            s.results[i] = Spin(s._flatWork[i]);
            return s;
        });
        return results[0];
    }

    [Benchmark]
    public long FlatRayonFor()
    {
        long[] results = new long[_flatWork.Length];
        Rayon.For(0, _flatWork.Length, (_flatWork, results), static (s, i) => s.results[i] = Spin(s._flatWork[i]));
        return results[0];
    }
}
