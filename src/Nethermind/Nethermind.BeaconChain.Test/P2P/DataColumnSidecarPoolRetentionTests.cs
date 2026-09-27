// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.P2P;

/// <summary>
/// The served maps evict the lowest slot first, so the slots a node can still serve completely are always one
/// suffix of the chain: a node that answers ByRange from that suffix never skips a column it was given.
/// </summary>
public class DataColumnSidecarPoolRetentionTests
{
    private const int Capacity = 4;
    private const ulong Column = 3;

    /// <summary>Under recency eviction, old-slot columns arriving late would push out the newest block's columns first.</summary>
    [Test]
    public void A_flood_of_lower_slot_columns_never_evicts_a_higher_slot_column_before_a_lower_one([Values] bool gloas)
    {
        DataColumnSidecarPool pool = new(Capacity);
        ulong[] held = [100, 101, 102, 103];
        foreach (ulong slot in held) Add(pool, gloas, slot);

        for (ulong slot = 0; slot < 50; slot++) Add(pool, gloas, slot);
        bool[] heldAfterFlood = [.. held.Select(slot => Holds(pool, gloas, slot))];

        Add(pool, gloas, 104);
        bool[] heldAfterNewer = [.. held.Append(104UL).Select(slot => Holds(pool, gloas, slot))];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(heldAfterFlood, Is.All.True, "a lower slot than every held one is refused, not admitted by evicting a higher one");
            Assert.That(heldAfterNewer, Is.EqualTo(new[] { false, true, true, true, true }), "a newer slot evicts the lowest held slot");
        }
    }

    [Test]
    public void Earliest_completely_servable_slot_is_the_first_slot_given_until_eviction_raises_it([Values] bool gloas)
    {
        DataColumnSidecarPool pool = new(Capacity);
        ulong beforeAny = pool.EarliestCompletelyServableSlot;

        Add(pool, gloas, 10, column: 0);
        Add(pool, gloas, 10, column: 1);
        Add(pool, gloas, 11, column: 0);
        Add(pool, gloas, 12, column: 0);
        ulong full = pool.EarliestCompletelyServableSlot;

        Add(pool, gloas, 13, column: 0);
        ulong afterPartialEviction = pool.EarliestCompletelyServableSlot;

        Add(pool, gloas, 5, column: 0);
        ulong afterRefusedLowSlot = pool.EarliestCompletelyServableSlot;

        Add(pool, gloas, 14, column: 0);
        Add(pool, gloas, 15, column: 0);
        ulong afterSecondSlotEvicted = pool.EarliestCompletelyServableSlot;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(beforeAny, Is.EqualTo(ulong.MaxValue), "nothing given yet, so no slot is known to be complete");
            Assert.That(full, Is.EqualTo(10UL));
            Assert.That(afterPartialEviction, Is.EqualTo(11UL), "slot 10 lost one of its two columns");
            Assert.That(afterRefusedLowSlot, Is.EqualTo(11UL), "a refused lower slot neither lowers nor raises the mark");
            Assert.That(afterSecondSlotEvicted, Is.EqualTo(12UL), "slot 10's last column, then slot 11's, went next");
        }
    }

    /// <summary>A slot below the first one given was never seen whole, so a late or refused old sidecar must not mark the slots in between complete.</summary>
    [TestCase(new ulong[] { 100, 101, 102, 103 }, 49UL, 100UL, TestName = "A refused old sidecar leaves the mark of a full pool")]
    [TestCase(new ulong[] { 100 }, 49UL, 100UL, TestName = "A late old sidecar leaves the mark of a pool with room")]
    [TestCase(new ulong[] { ulong.MaxValue }, 0UL, ulong.MaxValue, TestName = "A first sidecar at the last slot is not forgotten")]
    public void Earliest_completely_servable_slot_never_decreases(ulong[] held, ulong lateSlot, ulong expected)
    {
        foreach (bool gloas in new[] { false, true })
        {
            DataColumnSidecarPool pool = new(Capacity);
            foreach (ulong slot in held) Add(pool, gloas, slot);
            ulong before = pool.EarliestCompletelyServableSlot;

            Add(pool, gloas, lateSlot);

            Assert.That((before, pool.EarliestCompletelyServableSlot), Is.EqualTo((expected, expected)), gloas ? "gloas" : "fulu");
        }
    }

    /// <summary>A refused column is dropped once the recent set churns it out, so its slot must not be reported complete.</summary>
    [Test]
    public void A_slot_whose_column_was_refused_stays_below_the_mark_after_the_recent_set_drops_it([Values] bool gloas)
    {
        DataColumnSidecarPool pool = new(Capacity);
        Add(pool, gloas, 10);
        for (ulong slot = 20; slot < 20 + Capacity; slot++) Add(pool, gloas, slot);

        Add(pool, gloas, 15);
        for (ulong slot = 11; slot < 15; slot++) Add(pool, gloas, slot);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Holds(pool, gloas, 15), Is.False, "neither set still holds slot 15's column");
            Assert.That(pool.EarliestCompletelyServableSlot, Is.EqualTo(16UL), "so the mark sits above slot 15");
        }
    }

    /// <summary>
    /// Range sync adds verified columns for blocks below the head slots gossip already filled the pool with; the importer
    /// reads the same pool next, so a column the slot-ordered set refuses must still be found or sync cannot advance.
    /// </summary>
    [Test]
    public void A_column_below_every_held_slot_is_still_found_after_the_retained_set_refuses_it([Values] bool gloas)
    {
        DataColumnSidecarPool pool = new(Capacity);
        ulong[] held = [100, 101, 102, 103];
        foreach (ulong slot in held) Add(pool, gloas, slot);

        for (ulong column = 0; column < Capacity; column++) Add(pool, gloas, 50, column);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Enumerable.Range(0, Capacity).Select(c => Holds(pool, gloas, 50, (ulong)c)), Is.All.True, "the refused columns are held for import");
            Assert.That(held.Select(slot => Holds(pool, gloas, slot)), Is.All.True, "and still displace no higher slot");
            Assert.That(pool.EarliestCompletelyServableSlot, Is.EqualTo(100UL), "a column held only for import is not counted as retained");
        }
    }

    /// <summary>
    /// Gossip adds a Fulu sidecar before its proposer is checked, so sidecars for forged headers can fill the pool at the head
    /// slot; the lowest slot then evicts its oldest entry, so the genuine block's later column displaces a forged one.
    /// </summary>
    [Test]
    public void At_the_lowest_held_slot_a_new_column_evicts_the_oldest_one_of_that_slot([Values] bool gloas)
    {
        const ulong headSlot = 200;
        DataColumnSidecarPool pool = new(Capacity);
        Hash256[] forged = [.. Enumerable.Range(0, Capacity).Select(i => Keccak.Compute($"forged {i}"))];
        foreach (Hash256 root in forged) Add(pool, gloas, headSlot, root);
        Hash256 genuine = Keccak.Compute("genuine");
        Add(pool, gloas, headSlot, genuine);

        // Older-slot columns churn the recent set, so only the slot-ordered set can still hold the head slot's columns.
        for (ulong column = 0; column < Capacity; column++) Add(pool, gloas, headSlot - 1, RootFor(headSlot - 1), column);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Holds(pool, gloas, genuine), Is.True, "the newest column at the lowest slot is retained");
            Assert.That(forged.Select(root => Holds(pool, gloas, root)), Is.EqualTo(new[] { false, true, true, true }), "the oldest column of that slot made room");
        }
    }

    /// <summary>A Fulu slot evicted from its own map leaves the Gloas map's slots complete, but the mark is one suffix for both.</summary>
    [Test]
    public void Earliest_completely_servable_slot_spans_both_forks()
    {
        DataColumnSidecarPool pool = new(Capacity);
        Add(pool, gloas: false, 20);
        Add(pool, gloas: true, 30);
        ulong both = pool.EarliestCompletelyServableSlot;

        for (ulong slot = 21; slot <= 24; slot++) Add(pool, gloas: false, slot);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(both, Is.EqualTo(20UL), "the lowest slot either fork was given");
            Assert.That(pool.EarliestCompletelyServableSlot, Is.EqualTo(21UL), "the Fulu map evicted slot 20");
        }
    }

    [Test]
    public void Replacing_a_held_sidecar_keeps_one_entry_and_serves_the_replacement()
    {
        DataColumnSidecarPool pool = new(1);
        Hash256 root = RootFor(7);
        DataColumnSidecar first = Fulu(7, Column);
        DataColumnSidecar second = Fulu(7, Column);
        pool.Add(root, 7, first);
        pool.Add(root, 7, second);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pool.TryGet(root, Column, out DataColumnSidecar? held) ? held : null, Is.SameAs(second));
            Assert.That(pool.EarliestCompletelyServableSlot, Is.EqualTo(7UL), "a replacement is not an eviction");
        }
    }

    /// <summary>Readers and writers share the served maps; an unguarded read during an eviction corrupts or throws.</summary>
    [Test]
    [CancelAfter(60_000)]
    public async Task Reads_racing_evictions_never_fail(CancellationToken token)
    {
        const int Writers = 4;
        const int Readers = 4;
        const int Operations = 20_000;
        DataColumnSidecarPool pool = new(Capacity);
        using Barrier start = new(Writers + Readers);

        Task[] tasks =
        [
            .. Enumerable.Range(0, Writers).Select(w => Task.Run(() =>
            {
                start.SignalAndWait(token);
                for (int i = 0; i < Operations; i++)
                {
                    ulong slot = (ulong)(i * Writers + w);
                    Add(pool, gloas: (i & 1) == 0, slot, column: (ulong)(i % 8));
                }
            }, token)),
            .. Enumerable.Range(0, Readers).Select(r => Task.Run(() =>
            {
                start.SignalAndWait(token);
                for (int i = 0; i < Operations; i++)
                {
                    ulong slot = (ulong)(i * Writers + r % Writers);
                    Holds(pool, gloas: (i & 1) == 0, slot, column: (ulong)(i % 8));
                    _ = pool.EarliestCompletelyServableSlot;
                    _ = pool.SlotIndexCount;
                }
            }, token)),
        ];

        await Task.WhenAll(tasks);

        Assert.That(pool.SlotIndexCount, Is.LessThanOrEqualTo(2 * Capacity));
    }

    private static void Add(DataColumnSidecarPool pool, bool gloas, ulong slot, ulong column = Column) => Add(pool, gloas, slot, RootFor(slot), column);

    private static void Add(DataColumnSidecarPool pool, bool gloas, ulong slot, Hash256 root, ulong column = Column)
    {
        if (gloas)
        {
            pool.AddGloas(new DataColumnSidecarGloas { Index = column, Column = [], KzgProofs = [], Slot = slot, BeaconBlockRoot = root });
        }
        else
        {
            pool.Add(root, slot, Fulu(slot, column));
        }
    }

    private static bool Holds(DataColumnSidecarPool pool, bool gloas, ulong slot, ulong column = Column) => Holds(pool, gloas, RootFor(slot), column);

    private static bool Holds(DataColumnSidecarPool pool, bool gloas, Hash256 root, ulong column = Column) =>
        gloas ? pool.TryGetGloas(root, column, out _) : pool.TryGet(root, column, out _);

    private static DataColumnSidecar Fulu(ulong slot, ulong column) => new()
    {
        Index = column,
        Column = [],
        KzgCommitments = [],
        KzgProofs = [],
        SignedBlockHeader = new SignedBeaconBlockHeader { Message = new BeaconBlockHeader { Slot = slot } },
        KzgCommitmentsInclusionProof = [],
    };

    private static Hash256 RootFor(ulong slot)
    {
        byte[] bytes = new byte[32];
        BitConverter.TryWriteBytes(bytes, slot);
        return new Hash256(bytes);
    }
}
