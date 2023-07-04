// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using FluentAssertions;
using Nethermind.Core.Collections;
using NUnit.Framework;

namespace Nethermind.Core.Test.Collections
{
    [Parallelizable(ParallelScope.All)]
    public class JournalSetTests
    {
        [Test]
        public void Can_restore_snapshot()
        {
            JournalSet<int> journalSet = new();
            journalSet.AddRange(Enumerable.Range(0, 10));
            int snapshot = journalSet.TakeSnapshot();
            journalSet.AddRange(Enumerable.Range(10, 10));
            journalSet.Restore(snapshot);
            journalSet.Should().BeEquivalentTo(Enumerable.Range(0, 10));
        }

        [Test]
        public void Can_restore_empty_snapshot_on_empty()
        {
            JournalSet<int> journalSet = new() { };
            int snapshot = journalSet.TakeSnapshot();
            journalSet.Restore(snapshot);
            journalSet.Restore(snapshot);
            journalSet.Should().BeEquivalentTo(Enumerable.Empty<int>());
        }

        [Test]
        public void Can_restore_empty_snapshot()
        {
            JournalSet<int> journalSet = new() { };
            int snapshot = journalSet.TakeSnapshot();
            journalSet.AddRange(Enumerable.Range(0, 10));
            journalSet.Restore(snapshot);
            journalSet.Restore(snapshot);
            journalSet.Should().BeEquivalentTo(Enumerable.Empty<int>());
        }

        [Test]
        public void Snapshots_behave_as_sets()
        {
            JournalSet<int> journalSet = new();
            journalSet.AddRange(Enumerable.Range(0, 10));
            int snapshot = journalSet.TakeSnapshot();
            journalSet.AddRange(Enumerable.Range(0, 20));
            journalSet.Restore(snapshot);
            journalSet.Should().BeEquivalentTo(Enumerable.Range(0, 10));
        }
    }
}
