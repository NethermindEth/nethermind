// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

public class SnapshotTests
{
    [Test]
    public void MutableSnapshotCountsAndEstimatesAreSealedAtConstruction()
    {
        // The repository adds EstimateMemory() to its memory ledger when a snapshot is published and
        // subtracts a later call on removal: both sides must see the same value even if a straggling
        // writer (e.g. a trie-warmer job) mutates the content after publication, and serving sealed
        // counts avoids the all-stripe-locks ConcurrentDictionary.Count on live maps.
        ResourcePool pool = new(new FlatDbConfig());
        Snapshot snapshot = FlatTestHelpers.MakeSnapshot(pool, content =>
        {
            content.Accounts[new(TestItem.AddressA)] = new(1, 100);
            content.Storages[new((TestItem.AddressA, 1))] = new SlotValue(TestItem.KeccakA.Bytes);
        });

        long estimate = snapshot.EstimateMemory();
        long compactedEstimate = snapshot.EstimateCompactedMemory();
        Assert.That(snapshot.AccountsCount, Is.EqualTo(1));
        Assert.That(snapshot.StoragesCount, Is.EqualTo(1));

        // A write landing after publication must not move the sealed values.
        snapshot.Content.Accounts[new(TestItem.AddressB)] = new(2, 200);

        Assert.That(snapshot.AccountsCount, Is.EqualTo(1));
        Assert.That(snapshot.EstimateMemory(), Is.EqualTo(estimate));
        Assert.That(snapshot.EstimateCompactedMemory(), Is.EqualTo(compactedEstimate));

        snapshot.Dispose();
    }

    [Test]
    public void SealedEstimatesMatchLiveContentEstimates()
    {
        ResourcePool pool = new(new FlatDbConfig());
        Snapshot snapshot = FlatTestHelpers.MakeSnapshot(pool, content =>
        {
            content.Accounts[new(TestItem.AddressA)] = new(1, 100);
            content.SelfDestructedStorageAddresses[new(TestItem.AddressB)] = true;
        });

        Assert.That(snapshot.EstimateMemory(), Is.EqualTo(snapshot.Content.EstimateMemory()));
        Assert.That(snapshot.EstimateCompactedMemory(), Is.EqualTo(snapshot.Content.EstimateCompactedMemory()));

        snapshot.Dispose();
    }
}
