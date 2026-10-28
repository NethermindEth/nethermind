// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Collections;
using NUnit.Framework;

namespace Nethermind.Core.Test.Collections;

[Parallelizable(ParallelScope.All)]
public class OptimizedCollectionsTrimTests
{
    private const int LargeRound = 3_000;
    private const int SmallRound = 3;

    [Test]
    public void Dictionary_shrinks_after_a_run_of_sparse_clears()
    {
        OptimizedDictionary<int, int> map = [];
        Fill(map, LargeRound);
        map.ClearAndTrim();
        int grown = map.Capacity;

        for (int round = 1; round < SparseClearTrimmer.SparseClearsBeforeTrim; round++)
        {
            Fill(map, SmallRound);
            map.ClearAndTrim();
        }

        Assert.That(map.Capacity, Is.EqualTo(grown), "one round short of the run keeps the capacity");

        Fill(map, SmallRound);
        map.ClearAndTrim();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(grown, Is.GreaterThan(LargeRound));
            Assert.That(map.Capacity, Is.LessThan(SparseClearTrimmer.MinCapacity * 2));
            Assert.That(map.Count, Is.Zero);
        }
    }

    [Test]
    public void Hash_set_shrinks_after_a_run_of_sparse_clears()
    {
        OptimizedHashSet<int> set = [];
        Fill(set, LargeRound);
        set.ClearAndTrim();
        int grown = set.Capacity;

        for (int round = 0; round < SparseClearTrimmer.SparseClearsBeforeTrim; round++)
        {
            Fill(set, SmallRound);
            set.ClearAndTrim();
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(grown, Is.GreaterThan(LargeRound));
            Assert.That(set.Capacity, Is.LessThan(SparseClearTrimmer.MinCapacity * 2));
            Assert.That(set.Count, Is.Zero);
        }
    }

    [Test]
    public void Map_with_an_explicit_trim_capacity_is_never_shrunk_for_sparseness()
    {
        const int PooledCapacity = 512;
        OptimizedDictionary<int, int> map = [];
        OptimizedHashSet<int> set = [];
        Fill(map, PooledCapacity);
        Fill(set, PooledCapacity);
        map.ClearAndTrim(PooledCapacity, PooledCapacity);
        set.ClearAndTrim(PooledCapacity, PooledCapacity);
        int mapCapacity = map.Capacity;
        int setCapacity = set.Capacity;

        for (int round = 0; round < SparseClearTrimmer.SparseClearsBeforeTrim * 2; round++)
        {
            Fill(map, SmallRound);
            Fill(set, SmallRound);
            map.ClearAndTrim(PooledCapacity, PooledCapacity);
            set.ClearAndTrim(PooledCapacity, PooledCapacity);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mapCapacity, Is.GreaterThanOrEqualTo(PooledCapacity));
            Assert.That(map.Capacity, Is.EqualTo(mapCapacity));
            Assert.That(set.Capacity, Is.EqualTo(setCapacity));
        }
    }

    [Test]
    public void Map_just_above_the_floor_shrinks_to_the_floor()
    {
        OptimizedDictionary<int, int> map = [];
        Fill(map, SparseClearTrimmer.MinCapacity + 1);
        map.ClearAndTrim();
        int grown = map.Capacity;

        for (int round = 0; round < SparseClearTrimmer.SparseClearsBeforeTrim; round++)
        {
            Fill(map, 1);
            map.ClearAndTrim();
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(grown, Is.GreaterThan(SparseClearTrimmer.MinCapacity));
            Assert.That(map.Capacity, Is.EqualTo(SparseClearTrimmer.MinCapacity));
        }
    }

    [Test]
    public void Map_at_the_floor_is_left_alone()
    {
        OptimizedDictionary<int, int> map = new(SparseClearTrimmer.MinCapacity);
        int initial = map.Capacity;

        for (int round = 0; round < SparseClearTrimmer.SparseClearsBeforeTrim * 2; round++)
        {
            Fill(map, 1);
            map.ClearAndTrim();
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(initial, Is.EqualTo(SparseClearTrimmer.MinCapacity), "the floor is a size the map takes exactly");
            Assert.That(map.Capacity, Is.EqualTo(initial));
        }
    }

    [Test]
    public void Dictionary_that_fills_within_each_run_keeps_its_capacity()
    {
        OptimizedDictionary<int, int> map = [];
        Fill(map, LargeRound);
        map.ClearAndTrim();
        int grown = map.Capacity;

        for (int round = 1; round <= SparseClearTrimmer.SparseClearsBeforeTrim * 3; round++)
        {
            Fill(map, round % (SparseClearTrimmer.SparseClearsBeforeTrim - 1) == 0 ? LargeRound : SmallRound);
            map.ClearAndTrim();
            Assert.That(map.Capacity, Is.EqualTo(grown), $"round {round}");
        }
    }

    [Test]
    public void Empty_clears_neither_extend_nor_break_a_sparse_run()
    {
        OptimizedDictionary<int, int> map = [];
        Fill(map, LargeRound);
        map.ClearAndTrim();
        int grown = map.Capacity;

        for (int round = 1; round < SparseClearTrimmer.SparseClearsBeforeTrim; round++)
        {
            Fill(map, SmallRound);
            map.ClearAndTrim();
            map.ClearAndTrim();
        }

        Assert.That(map.Capacity, Is.EqualTo(grown));
        Fill(map, SmallRound);
        map.ClearAndTrim();
        Assert.That(map.Capacity, Is.LessThan(grown));
    }

    [Test]
    public void Shrunk_map_fits_the_largest_sparse_round_without_regrowing()
    {
        const int PeakRound = 400;
        OptimizedDictionary<int, int> map = [];
        Fill(map, LargeRound);
        map.ClearAndTrim();

        for (int round = 0; round < SparseClearTrimmer.SparseClearsBeforeTrim; round++)
        {
            Fill(map, round == 7 ? PeakRound : SmallRound);
            map.ClearAndTrim();
        }

        int shrunk = map.Capacity;
        Fill(map, PeakRound);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(shrunk, Is.GreaterThanOrEqualTo(PeakRound * 2));
            Assert.That(map.Capacity, Is.EqualTo(shrunk));
        }
    }

    [Test]
    public void Shrinking_keeps_contents_and_enumeration_order_of_the_bcl_dictionary([Values(1, 2, 3)] int seed)
    {
        Random random = new(seed);
        Dictionary<int, int> expected = [];
        OptimizedDictionary<int, int> actual = [];
        int trims = 0;

        for (int round = 0; round < SparseClearTrimmer.SparseClearsBeforeTrim * 4; round++)
        {
            int size = round % 300 == 0 ? LargeRound : random.Next(1, 40);
            for (int i = 0; i < size; i++)
            {
                int key = random.Next(LargeRound * 2);
                actual[key] = expected[key] = i;
                if (random.Next(4) == 0)
                {
                    int removed = random.Next(LargeRound * 2);
                    Assert.That(actual.Remove(removed), Is.EqualTo(expected.Remove(removed)));
                }
            }

            Assert.That(actual.ToArray(), Is.EqualTo(expected.ToArray()), $"round {round}");
            int capacity = actual.Capacity;
            actual.ClearAndTrim();
            expected.Clear();
            if (actual.Capacity < capacity) trims++;
        }

        Assert.That(trims, Is.GreaterThan(0), "the run must include a sparse shrink");
    }

    private static void Fill(OptimizedDictionary<int, int> map, int count)
    {
        for (int i = 0; i < count; i++) map[i] = i;
    }

    private static void Fill(OptimizedHashSet<int> set, int count)
    {
        for (int i = 0; i < count; i++) set.Add(i);
    }
}
