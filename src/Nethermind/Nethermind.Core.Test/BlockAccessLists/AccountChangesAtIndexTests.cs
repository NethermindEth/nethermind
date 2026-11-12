// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;
using CoreCollectionExtensions = Nethermind.Core.Collections.CollectionExtensions;

namespace Nethermind.Core.Test.BlockAccessLists;

[TestFixture]
public class AccountChangesAtIndexTests
{
    [Test]
    public void Account_growth_and_reuse_preserve_distinct_read_sets([Values(1, 65)] int count)
    {
        BlockAccessListAtIndex slice = new();
        Address[] addresses = new Address[count];
        for (int i = 0; i < count; i++) addresses[i] = new Address(i.ToString("x40"));

        for (int round = 0; round < 2; round++)
        {
            for (int i = 0; i < count; i++)
                slice.AddStorageRead(addresses[i], (UInt256)(100 * round + i));

            Assert.That(slice.AccountCount, Is.EqualTo(count));
            for (int i = 0; i < count; i++)
            {
                AccountChangesAtIndex account = slice.GetAccountChanges(addresses[i])!;
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(account.Address, Is.EqualTo(addresses[i]));
                    Assert.That(account.StorageReads, Is.EquivalentTo(new UInt256[] { (UInt256)(100 * round + i) }));
                    Assert.That(account.StorageChanges, Is.Empty);
                }
            }
            slice.Clear();
        }
    }

    [Test]
    public void Storage_changes_list_only_changed_slots_among_those_holding_a_prestate()
    {
        BlockAccessListAtIndex slice = new();
        slice.AddStorageChange(TestItem.AddressA, (UInt256)1, before: UInt256.Zero, after: (UInt256)5);
        slice.AddStorageChange(TestItem.AddressA, (UInt256)2, before: (UInt256)7, after: (UInt256)8);
        slice.AddStorageChange(TestItem.AddressA, (UInt256)2, before: (UInt256)8, after: (UInt256)7);
        AccountChangesAtIndex account = slice.GetAccountChanges(TestItem.AddressA)!;
        account.SeedPreTxStorage((UInt256)3, (UInt256)9);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(account.StorageChangeCount, Is.EqualTo(1));
            Assert.That(account.StorageChanges.Select(static change => change.Key), Is.EqualTo(new[] { (UInt256)1 }));
            Assert.That(account.StorageChanges[(UInt256)1], Is.EqualTo(new StorageChange(0, (UInt256)5)));
            Assert.That(account.HasStorageChange((UInt256)2), Is.False);
            Assert.That(account.StorageReads, Does.Contain((UInt256)2));
            Assert.That(account.TryGetPreTxStorage((UInt256)2, out UInt256 written), Is.True);
            Assert.That(written, Is.EqualTo((UInt256)7));
            Assert.That(account.TryGetPreTxStorage((UInt256)3, out UInt256 seeded), Is.True);
            Assert.That(seeded, Is.EqualTo((UInt256)9));
        }

        account.ClearStorageChanges();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(account.StorageChangeCount, Is.Zero);
            Assert.That(account.StorageChanges, Is.Empty);
            Assert.That(account.TryGetPreTxStorage((UInt256)1, out UInt256 cleared), Is.True);
            Assert.That(cleared, Is.EqualTo(UInt256.Zero));
        }
    }

    [Test]
    public void Clear_trims_oversized_account_changes_before_pooling()
    {
        const int EntryCount = CoreCollectionExtensions.DefaultTrimAboveCapacity + 1;

        BlockAccessListAtIndex blockAccessList = new();
        AccountChangesAtIndex accountChanges = blockAccessList.RecordReadAndGet(TestItem.AddressA);
        StorageChange storageChange = new(0, UInt256.One);
        for (int i = 0; i < EntryCount; i++)
        {
            UInt256 slot = (UInt256)i;
            accountChanges.SetStorageChange(slot, storageChange);
            accountChanges.AddStorageRead(slot);
            accountChanges.SeedPreTxStorage(slot, UInt256.One);
        }

        int changesCapacityBeforeClear = accountChanges.StorageChanges.Capacity;
        int readsCapacityBeforeClear = accountChanges.StorageReads.Capacity;

        blockAccessList.Clear();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(blockAccessList.AccountCount, Is.Zero);
            Assert.That(accountChanges.StorageChanges, Is.Empty);
            Assert.That(accountChanges.StorageChanges.Capacity, Is.GreaterThan(0));
            Assert.That(accountChanges.StorageChanges.Capacity, Is.LessThan(changesCapacityBeforeClear));
            Assert.That(accountChanges.StorageReads, Is.Empty);
            Assert.That(accountChanges.StorageReads.Capacity, Is.GreaterThan(0));
            Assert.That(accountChanges.StorageReads.Capacity, Is.LessThan(readsCapacityBeforeClear));
        }

        AccountChangesAtIndex reused = blockAccessList.RecordReadAndGet(TestItem.AddressB);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reused, Is.SameAs(accountChanges));
            Assert.That(reused.Address, Is.EqualTo(TestItem.AddressB));
        }

        // The reused entry holds no prestate from its previous account: the first write captures it, later ones keep it.
        Assert.That(reused.TryGetPreTxStorage(UInt256.Zero, out _), Is.False);
        blockAccessList.AddStorageChange(TestItem.AddressB, UInt256.Zero, before: 77, after: 1);
        blockAccessList.AddStorageChange(TestItem.AddressB, UInt256.Zero, before: 1, after: 99);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reused.TryGetPreTxStorage(UInt256.Zero, out UInt256 original), Is.True);
            Assert.That(original, Is.EqualTo((UInt256)77));
            Assert.That(reused.StorageChanges[UInt256.Zero].Value, Is.EqualTo((UInt256)99));
        }
    }
}
