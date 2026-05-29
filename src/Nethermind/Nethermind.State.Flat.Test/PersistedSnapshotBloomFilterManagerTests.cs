// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.State.Flat.Persistence.BloomFilter;
using Nethermind.State.Flat.PersistedSnapshots;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class PersistedSnapshotBloomFilterManagerTests
{
    private static StateId State(long blockNumber) => new(blockNumber, Keccak.Compute($"s{blockNumber}"));

    /// <summary>
    /// The bundle fetch (<see cref="PersistedSnapshotBloomFilterManager.LeaseOrSentinel(StateId, StateId)"/>)
    /// must only hand out a bloom that covers the full snapshot range. A registration
    /// race can leave a narrower bloom at a wider snapshot's <c>To</c> slot; leasing it
    /// would under-cover and silently drop reads, so the fetch must fall back to the
    /// always-true sentinel.
    /// </summary>
    [Test]
    public void LeaseOrSentinel_rejects_bloom_that_does_not_cover_full_range()
    {
        using PersistedSnapshotBloomFilterManager manager = new();

        // Base bloom covering (s3, s4] registered at the s4 slot.
        PersistedSnapshotBloom registered = new(State(3), State(4), new BloomFilter(16, 10.0));
        manager.Register(registered);

        PersistedSnapshotBloom covered = manager.LeaseOrSentinel(State(3), State(4));
        PersistedSnapshotBloom underCovered = manager.LeaseOrSentinel(State(0), State(4));
        PersistedSnapshotBloom missed = manager.LeaseOrSentinel(State(0), State(9));

        Assert.Multiple(() =>
        {
            // Exact coverage — the real registered bloom is leased.
            Assert.That(covered, Is.SameAs(registered), "bloom covering the full range must be leased");
            // Narrower bloom under-covers the wider snapshot range — fall back to sentinel.
            Assert.That(underCovered, Is.SameAs(PersistedSnapshotBloom.AlwaysTrue), "under-covering bloom must be rejected");
            // No entry for the To slot — fall back to sentinel.
            Assert.That(missed, Is.SameAs(PersistedSnapshotBloom.AlwaysTrue), "missing slot must return sentinel");
        });

        if (!ReferenceEquals(covered, PersistedSnapshotBloom.AlwaysTrue)) covered.Dispose();
    }
}
