// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Reflection;
using BenchmarkDotNet.Attributes;
using Nethermind.Int256;
using Nethermind.TxPool.Filters;

namespace Nethermind.Benchmarks.TxPool;

[MemoryDiagnoser]
public class KeyedNonceOverlapBenchmarks
{
    private delegate bool Intersection(ReadOnlySpan<UInt256> incoming, ReadOnlySpan<UInt256> pending, ref bool? incomingWellFormed);

    private UInt256[] _incoming = [];
    private UInt256[] _pending = [];
    private Intersection _production = null!;
    private readonly Intersection _cartesian = Cartesian;

    [Params(1, 2, 4, 8, 16)]
    public int KeyCount { get; set; }

    [ParamsAllValues]
    public OverlapPosition Position { get; set; }

    [Params(false, true)]
    public bool Sorted { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        Type filter = typeof(IIncomingTxFilter).Assembly.GetType("Nethermind.TxPool.Filters.KeyedNonceDisjointnessFilter", throwOnError: true)!;
        MethodInfo intersection = filter.GetMethod("Intersects", BindingFlags.NonPublic | BindingFlags.Static)!;
        _production = intersection.CreateDelegate<Intersection>();

        _incoming = new UInt256[KeyCount];
        _pending = new UInt256[KeyCount];
        for (int i = 0; i < KeyCount; i++)
        {
            _incoming[i] = (UInt256)(2 * i);
            _pending[i] = (UInt256)(2 * i + 1);
        }
        int overlapIndex = Position switch
        {
            OverlapPosition.Disjoint => -1,
            OverlapPosition.First => 0,
            OverlapPosition.Middle => KeyCount / 2,
            OverlapPosition.Last => KeyCount - 1,
            _ => throw new ArgumentOutOfRangeException()
        };
        if (overlapIndex >= 0) _pending[overlapIndex] = _incoming[overlapIndex];
        if (!Sorted) Array.Reverse(_incoming);

        bool? incomingWellFormed = null;
        if (_production(_incoming, _pending, ref incomingWellFormed) != _cartesian(_incoming, _pending, ref incomingWellFormed))
        {
            throw new InvalidOperationException("The production intersection differs from the reference.");
        }
    }

    [Benchmark(Baseline = true)]
    public bool CartesianIntersection()
    {
        bool? incomingWellFormed = null;
        return _cartesian(_incoming, _pending, ref incomingWellFormed);
    }

    [Benchmark]
    public bool ProductionIntersection()
    {
        bool? incomingWellFormed = null;
        return _production(_incoming, _pending, ref incomingWellFormed);
    }

    private static bool Cartesian(ReadOnlySpan<UInt256> incoming, ReadOnlySpan<UInt256> pending, ref bool? incomingWellFormed)
    {
        foreach (UInt256 key in incoming)
        {
            foreach (UInt256 other in pending)
            {
                if (key == other) return true;
            }
        }
        return false;
    }

    public enum OverlapPosition
    {
        Disjoint,
        First,
        Middle,
        Last
    }
}
