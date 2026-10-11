// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Collections;
using NUnit.Framework;

namespace Nethermind.Core.ZkEvm.Test.Collections;

/// <summary>The guest's journals reset their item lists without zeroing them, then must behave as freshly cleared.</summary>
public class GuestJournalClearTests
{
    private static readonly Address _a = new("0x000000000000000000000000000000000000000a");
    private static readonly Address _b = new("0x000000000000000000000000000000000000000b");
    private static readonly Address _c = new("0x000000000000000000000000000000000000000c");

    [Test]
    public void Journal_set_reuses_cleared_slots()
    {
        JournalSet<Address> set = new(Address.EqualityComparer) { _a, _b };

        set.Clear();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(set, Is.Empty);
            Assert.That(set.Contains(_a), Is.False);
        }

        set.Add(_c);
        int snapshot = set.TakeSnapshot();
        set.Add(_a);
        set.Add(_b);
        set.Restore(snapshot);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(set, Is.EqualTo(new[] { _c }));
            Assert.That(set.Contains(_a), Is.False);
        }
    }

    [Test]
    public void Journal_collection_reuses_cleared_slots()
    {
        JournalCollection<string> collection = ["first", "second"];

        collection.Clear();
        collection.Add("third");

        Assert.That(collection.ToArray(), Is.EqualTo(new[] { "third" }));
    }

    [Test]
    public void Clearing_invalidates_running_enumerators([Values] bool set)
    {
        ICollection<Address> journal = set ? new JournalSet<Address>(Address.EqualityComparer) : new JournalCollection<Address>();
        journal.Add(_a);
        journal.Add(_b);

        Assert.Throws<InvalidOperationException>(() =>
        {
            foreach (Address _ in journal)
                journal.Clear();
        });
    }
}
