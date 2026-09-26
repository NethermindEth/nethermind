// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Store.Test;

/// <remarks><see cref="Db.Metrics.StorageCleared"/> is process-wide, and NUnit still overlaps parallel tests with a
/// <c>[NonParallelizable]</c> method or nested fixture; only a top-level non-parallel fixture runs alone.</remarks>
[TestFixture(false)]
[TestFixture(true)]
[NonParallelizable]
public class StorageClearedMetricTests(bool useFlat)
{
    [TestCase(false, TestName = "StorageClearedMetric_CountsClearStorage")]
    [TestCase(true, TestName = "StorageClearedMetric_CountsMarkStorageDestroyed")]
    public void StorageClearedMetric_CountsOnlyCommittedClearOfNonEmptyStorage(bool markStorageDestroyed)
    {
        using StorageProviderTests.Context ctx = new(useFlat);
        WorldState provider = ctx.StateProvider;
        StorageCell persistedCell = new(ctx.Address1, 1);
        Address freshAddress = TestItem.AddressA;
        StorageCell freshCell = new(freshAddress, 1);
        long storageClearedBefore = Db.Metrics.StorageCleared;

        provider.Get(persistedCell, out UInt256 persistedValue);
        Assert.That(persistedValue, Is.EqualTo(UInt256.Zero));
        provider.Set(persistedCell, UInt256.One);
        provider.Commit(Frontier.Instance);
        long storageClearedAfterEmptyStorageReadAndWrite = Db.Metrics.StorageCleared;

        provider.Set(new StorageCell(ctx.Address2, 1), UInt256.One);
        provider.MarkStorageDestroyed(ctx.Address2);
        provider.Commit(Frontier.Instance);
        long storageClearedAfterDestroyingUnpersistedStorage = Db.Metrics.StorageCleared;

        Assert.That(provider.AccountExists(freshAddress), Is.False);
        provider.ClearStorage(freshAddress);
        provider.CreateAccount(freshAddress, 0);
        provider.Set(freshCell, UInt256.One);
        provider.Commit(Frontier.Instance);
        long storageClearedAfterFreshAddressClear = Db.Metrics.StorageCleared;

        Snapshot beforeRevertedClear = provider.TakeSnapshot();
        provider.ClearStorage(ctx.Address1);
        provider.Restore(beforeRevertedClear);
        provider.Commit(Frontier.Instance);
        long storageClearedAfterRevertedClear = Db.Metrics.StorageCleared;

        if (markStorageDestroyed)
            provider.MarkStorageDestroyed(ctx.Address1);
        else
            provider.ClearStorage(ctx.Address1);

        provider.Commit(Frontier.Instance, commitRoots: false);
        long storageClearedBeforeRootCommit = Db.Metrics.StorageCleared;
        provider.Commit(Frontier.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(storageClearedAfterEmptyStorageReadAndWrite, Is.EqualTo(storageClearedBefore));
            Assert.That(storageClearedAfterDestroyingUnpersistedStorage, Is.EqualTo(storageClearedBefore));
            Assert.That(storageClearedAfterFreshAddressClear, Is.EqualTo(storageClearedBefore));
            Assert.That(storageClearedAfterRevertedClear, Is.EqualTo(storageClearedBefore));
            Assert.That(storageClearedBeforeRootCommit, Is.EqualTo(storageClearedBefore));
            Assert.That(Db.Metrics.StorageCleared, Is.EqualTo(storageClearedBefore + 1));
        }
    }

    [Test]
    public void StorageClearedMetric_CountsEachContractInParallelFlush()
    {
        using StorageProviderTests.Context ctx = new(useFlat);
        WorldState provider = ctx.StateProvider;
        Address[] addresses = [ctx.Address1, ctx.Address2, TestItem.AddressA];

        provider.CreateAccount(TestItem.AddressA, 0);
        foreach (Address address in addresses)
            provider.Set(new StorageCell(address, 1), UInt256.One);

        provider.Commit(Frontier.Instance);
        long storageClearedBefore = Db.Metrics.StorageCleared;

        foreach (Address address in addresses)
            provider.ClearStorage(address);

        provider.Commit(Frontier.Instance);

        Assert.That(Db.Metrics.StorageCleared, Is.EqualTo(storageClearedBefore + addresses.Length));
    }
}
