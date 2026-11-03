// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Db.Rocks;
using NUnit.Framework;

namespace Nethermind.Db.Test;

[Parallelizable(ParallelScope.All)]
public class StripedLeaseCountTests
{
    [Test]
    public void Count_balances_leases_returned_from_the_current_slot()
    {
        StripedLeaseCount count = new();
        const int leases = 10_000;

        Parallel.For(0, leases, _ => count.Acquire());
        Assert.That(count.Count, Is.EqualTo(leases));

        Parallel.For(0, leases, _ => count.Release(StripedLeaseCount.CurrentSlot));
        Assert.That(count.Count, Is.Zero);
    }
}
