// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class SetAssociativeTableTests
{
    private const int Ways = 4;

    [Test]
    public void An_addition_that_lost_the_last_room_to_another_drops_no_entry_in_use()
    {
        SetAssociativeTable<TestEntry> table = new(sets: 1, Ways, keepsUsedEntries: true);
        TestEntry[] inUse = Enumerable.Range(0, Ways - 1).Select(i => table.GetOrAdd(new TestEntry(i))).ToArray();
        Assert.That(table.HasRoomFor(TestEntry.Hash), Is.True, "one free way");

        // Another addition takes the free way between the check and this addition.
        TestEntry other = table.GetOrAdd(new TestEntry(Ways - 1));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(table.TryGetOrAdd(new TestEntry(Ways)), Is.Null);
            foreach (TestEntry entry in inUse.Append(other)) Assert.That(Find(table, entry), Is.SameAs(entry), $"entry {entry.Id}");
        }
    }

    [Test]
    public void Racing_additions_fill_the_last_room_once_and_drop_no_entry_in_use()
    {
        const int threads = 8;
        for (int round = 0; round < 200; round++)
        {
            SetAssociativeTable<TestEntry> table = new(sets: 1, Ways, keepsUsedEntries: true);
            TestEntry[] inUse = Enumerable.Range(0, Ways - 1).Select(i => table.GetOrAdd(new TestEntry(i))).ToArray();

            using Barrier start = new(threads);
            Thread[] adders = Enumerable.Range(0, threads).Select(t => new Thread(() =>
            {
                start.SignalAndWait();
                if (table.HasRoomFor(TestEntry.Hash)) table.TryGetOrAdd(new TestEntry(100 + t));
            })).ToArray();
            foreach (Thread adder in adders) adder.Start();
            foreach (Thread adder in adders) adder.Join();

            // Fewer refusals than age the set, so the entries in use keep their marks.
            Assert.That(table.RefusalsBeforeAging, Is.GreaterThan(threads - 1));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(table.Count, Is.EqualTo(Ways), $"round {round}");
                foreach (TestEntry entry in inUse) Assert.That(Find(table, entry), Is.SameAs(entry), $"round {round}, entry {entry.Id}");
            }
        }
    }

    [Test]
    public void A_set_without_room_ages_after_its_refusals_and_then_takes_the_entry()
    {
        SetAssociativeTable<TestEntry> table = new(sets: 1, Ways, keepsUsedEntries: true);
        for (int i = 0; i < Ways; i++) table.GetOrAdd(new TestEntry(i));

        TestEntry entry = new(Ways);
        for (int refusal = 1; refusal <= table.RefusalsBeforeAging; refusal++)
        {
            Assert.That(table.TryGetOrAdd(entry), Is.Null, $"refusal {refusal}");
        }

        Assert.That(table.TryGetOrAdd(entry), Is.SameAs(entry), "after the set aged");
    }

    [Test]
    public void An_entry_already_in_a_set_without_room_is_returned()
    {
        SetAssociativeTable<TestEntry> table = new(sets: 1, Ways, keepsUsedEntries: true);
        TestEntry[] entries = Enumerable.Range(0, Ways).Select(i => table.GetOrAdd(new TestEntry(i))).ToArray();

        Assert.That(table.TryGetOrAdd(new TestEntry(0)), Is.SameAs(entries[0]));
    }

    [Test]
    public void A_table_that_does_not_keep_used_entries_always_adds()
    {
        SetAssociativeTable<TestEntry> table = new(sets: 1, Ways);
        for (int i = 0; i < Ways; i++) table.GetOrAdd(new TestEntry(i));

        TestEntry entry = new(Ways);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(table.TryGetOrAdd(entry), Is.SameAs(entry));
            Assert.That(Find(table, new TestEntry(0)), Is.Null, "the oldest fell off");
        }
    }

    private static TestEntry? Find(SetAssociativeTable<TestEntry> table, TestEntry entry) => table.Find(entry.Key, TestEntry.Hash);

    // All test entries share one hash, so they compete for the same set.
    private sealed class TestEntry(int id) : SetAssociativeEntry(Hash)
    {
        public const int Hash = 0x2A;

        private readonly byte[] _key = BitConverter.GetBytes(id);

        public int Id { get; } = id;

        public override ReadOnlySpan<byte> Key => _key;
    }
}
