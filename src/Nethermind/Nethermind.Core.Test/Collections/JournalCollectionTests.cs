// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using FluentAssertions;
using Nethermind.Core.Collections;
using NUnit.Framework;

namespace Nethermind.Core.Test.Collections
{
    [Parallelizable(ParallelScope.All)]
    public class JournalCollectionTests
    {
        [Test]
        public void Can_restore_snapshot()
        {
            JournalCollection<int> journal = new();
            journal.AddRange(Enumerable.Range(0, 10));
            int snapshot = journal.TakeSnapshot();
            journal.AddRange(Enumerable.Range(10, 10));
            journal.Restore(snapshot);
            journal.Should().BeEquivalentTo(Enumerable.Range(0, 10));
        }

        [Test]
        public void Can_restore_empty_snapshot()
        {
            JournalCollection<int> journal = new() { };
            int snapshot = journal.TakeSnapshot();
            journal.Restore(snapshot);
            journal.Restore(snapshot);
            journal.Should().BeEquivalentTo(Enumerable.Empty<int>());
        }
    }
}
