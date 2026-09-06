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
            accountChanges.GetOrCapturePreTxStorage(slot, UInt256.One);
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
    }
}
