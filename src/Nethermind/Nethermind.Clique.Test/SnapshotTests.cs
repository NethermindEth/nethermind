// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.Clique;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Clique.Test;

public class SnapshotTests
{
    [Test]
    public void Changing_a_cloned_tally_leaves_the_original_vote_count_intact()
    {
        Address candidate = TestItem.AddressA;
        Dictionary<Address, Tally> tallies = new() { [candidate] = new Tally(true) { Votes = 2 } };
        Snapshot original = new(1, TestItem.KeccakA, [], tallies);
        Snapshot clone = (Snapshot)original.Clone();

        clone.Tally[candidate].Votes++;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(original.Tally[candidate].Votes, Is.EqualTo(2));
            Assert.That(clone.Tally[candidate].Votes, Is.EqualTo(3));
            Assert.That(clone.Tally[candidate].Authorize, Is.True);
        }
    }
}
