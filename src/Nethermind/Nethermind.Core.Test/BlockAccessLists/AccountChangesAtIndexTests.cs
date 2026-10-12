// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
            accountChanges.GetOrCapturePreTxStorage(slot, UInt256.One, 0, out _);
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

        Assert.That(reused.GetOrCapturePreTxStorage(UInt256.Zero, (UInt256)77, 0, out UInt256 original), Is.True);
        Assert.That(original, Is.EqualTo((UInt256)77));
        Assert.That(reused.GetOrCapturePreTxStorage(UInt256.Zero, (UInt256)99, 0, out original), Is.False);
        Assert.That(original, Is.EqualTo((UInt256)77));
    }
}
