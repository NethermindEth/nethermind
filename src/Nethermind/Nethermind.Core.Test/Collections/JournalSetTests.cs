//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

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
