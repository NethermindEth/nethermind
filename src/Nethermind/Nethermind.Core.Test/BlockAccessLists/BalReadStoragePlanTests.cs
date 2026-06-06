// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Core.Test.BlockAccessLists;

/// <summary>
/// Pins the dense read-ordinal model: prefix-sum <c>ReadBase</c>, global ordinals, and the tiered
/// slot lookup (monotonic cursor / linear scan / binary search) all agree, and undeclared slots or
/// absent accounts report no ordinal.
/// </summary>
[TestFixture]
public class BalReadStoragePlanTests
{
    // A: 3 reads (linear path). B: 21 reads (binary-search path, > LinearScanThreshold). C: none.
    private static readonly UInt256[] ReadsA = [2, 5, 9];
    private static readonly UInt256[] ReadsB = Enumerable.Range(100, 21).Select(i => (UInt256)i).ToArray();

    private static BalReadStoragePlan BuildPlan() => BalReadStoragePlan.Build(
        Build.A.BlockAccessList
            .WithAccountChanges(
                Build.An.AccountChanges.WithAddress(TestItem.AddressA).WithStorageReads(ReadsA).TestObject,
                Build.An.AccountChanges.WithAddress(TestItem.AddressB).WithStorageReads(ReadsB).TestObject,
                Build.An.AccountChanges.WithAddress(TestItem.AddressC).TestObject)
            .TestObject);

    [Test]
    public void Read_bases_are_prefix_sums_in_plan_order()
    {
        BalReadStoragePlan plan = BuildPlan();

        Assert.That(plan.AccountCount, Is.EqualTo(3));
        Assert.That(plan.TotalReads, Is.EqualTo(ReadsA.Length + ReadsB.Length)); // 24

        // ReadBase is the running prefix sum over accounts in plan order, regardless of which
        // address the BAL happened to sort first.
        int expectedBase = 0;
        for (int i = 0; i < plan.AccountCount; i++)
        {
            Assert.That(plan.GetAccount(i).ReadBase, Is.EqualTo(expectedBase));
            expectedBase += plan.GetAccount(i).Account.StorageReads.Length;
        }
        Assert.That(expectedBase, Is.EqualTo(plan.TotalReads));
    }

    [Test]
    public void Global_ordinals_partition_the_space_account_relative()
    {
        BalReadStoragePlan plan = BuildPlan();

        // Every declared read maps to ReadBase + localIndex, and the ordinals tile [0, TotalReads)
        // exactly once (the empty account contributes none).
        bool[] seen = new bool[plan.TotalReads];
        foreach ((Address address, UInt256[] reads) in new[] { (TestItem.AddressA, ReadsA), (TestItem.AddressB, ReadsB) })
        {
            Assert.That(plan.TryGetAccountIndex(address, out int accountIndex), Is.True);
            int readBase = plan.GetAccount(accountIndex).ReadBase;

            for (int j = 0; j < reads.Length; j++)
            {
                Assert.That(plan.TryGetGlobalReadOrdinal(address, reads[j], out int g), Is.True);
                Assert.That(g, Is.EqualTo(readBase + j));
                Assert.That(seen[g], Is.False, $"ordinal {g} assigned twice");
                seen[g] = true;
            }
        }

        Assert.That(seen.Count(b => b), Is.EqualTo(plan.TotalReads));
    }

    [Test]
    public void Undeclared_slot_and_absent_account_report_no_ordinal()
    {
        BalReadStoragePlan plan = BuildPlan();

        Assert.That(plan.TryGetGlobalReadOrdinal(TestItem.AddressA, 7, out int undeclared), Is.False);
        Assert.That(undeclared, Is.EqualTo(-1));

        Assert.That(plan.TryGetGlobalReadOrdinal(TestItem.AddressD, 2, out int absentAccount), Is.False);
        Assert.That(absentAccount, Is.EqualTo(-1));
    }

    [Test]
    public void Ascending_stream_advances_the_cursor_in_order()
    {
        BalReadStoragePlan plan = BuildPlan();
        Assert.That(plan.TryGetAccountIndex(TestItem.AddressB, out int b), Is.True);

        int cursor = -1;
        for (int j = 0; j < ReadsB.Length; j++)
        {
            Assert.That(plan.TryGetReadLocalIndex(b, ReadsB[j], ref cursor, out int localIndex), Is.True);
            Assert.That(localIndex, Is.EqualTo(j));
            Assert.That(cursor, Is.EqualTo(j)); // cursor tracks the last hit, enabling the next O(1) step
        }
    }

    [Test]
    public void Out_of_order_lookup_falls_back_to_search_without_corrupting_cursor()
    {
        BalReadStoragePlan plan = BuildPlan();
        Assert.That(plan.TryGetAccountIndex(TestItem.AddressB, out int b), Is.True);

        int cursor = -1;
        // Jump straight to the last slot (binary-search path), then to the first (still resolvable).
        Assert.That(plan.TryGetReadLocalIndex(b, 120, ref cursor, out int last), Is.True);
        Assert.That(last, Is.EqualTo(20));
        Assert.That(plan.TryGetReadLocalIndex(b, 100, ref cursor, out int first), Is.True);
        Assert.That(first, Is.EqualTo(0));
    }
}
