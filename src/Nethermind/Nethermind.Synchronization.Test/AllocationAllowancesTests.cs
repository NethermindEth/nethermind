// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Synchronization.Peers;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test
{
    [Parallelizable(ParallelScope.All)]
    public class AllocationAllowancesTests
    {
        private static readonly AllocationContexts[] SingleBitContextCases =
        [
            AllocationContexts.Headers,
            AllocationContexts.Bodies,
            AllocationContexts.Receipts,
            AllocationContexts.State,
            AllocationContexts.Snap,
            AllocationContexts.ForwardHeader,
        ];

        [TestCaseSource(nameof(SingleBitContextCases))]
        public void Indexer_round_trips(AllocationContexts ctx)
        {
            AllocationAllowances a = AllocationAllowances.Single;
            Assert.That(a[ctx], Is.EqualTo(1), "Single is all-ones");

            a[ctx] = 7;
            Assert.That(a[ctx], Is.EqualTo(7));
        }

        [Test]
        public void Default_matches_production_defaults()
        {
            // Production: SyncPeerPool builds (headers: 1, others: AllocationSlots) — default 2.
            AllocationAllowances d = AllocationAllowances.Default;
            Assert.That(d.Headers, Is.EqualTo(1));
            Assert.That(d.Bodies, Is.EqualTo(2));
            Assert.That(d.Receipts, Is.EqualTo(2));
            Assert.That(d.State, Is.EqualTo(2));
            Assert.That(d.Snap, Is.EqualTo(2));
            Assert.That(d.ForwardHeader, Is.EqualTo(2));
        }
    }
}
