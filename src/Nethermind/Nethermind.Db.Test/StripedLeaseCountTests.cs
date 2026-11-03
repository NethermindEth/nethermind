// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Db.Rocks;
using NUnit.Framework;

namespace Nethermind.Db.Test;

[Parallelizable(ParallelScope.All)]
public class StripedLeaseCountTests
{
    [Test]
    public void Count_includes_leases_taken_on_every_core_and_returned_on_others()
    {
        StripedLeaseCount count = new();
        const int leases = 10_000;

        Parallel.For(0, leases, _ => count.Acquire());
        Assert.That(count.Count, Is.EqualTo(leases));

        Parallel.For(0, leases, _ => count.Release(StripedLeaseCount.CurrentSlot));
        Assert.That(count.Count, Is.Zero);
    }

    [Test]
    public void A_lease_returned_to_its_own_slot_balances_the_count()
    {
        StripedLeaseCount count = new();

        Parallel.For(0, 10_000, _ =>
        {
            int slot = count.Acquire();
            Thread.Yield();
            count.Release(slot);
        });

        Assert.That(count.Count, Is.Zero);
    }

    [Test]
    public void New_count_is_zero() => Assert.That(new StripedLeaseCount().Count, Is.Zero);
}
