// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
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
            a[ctx].Should().Be(1, "Single is all-ones");

            a[ctx] = 7;
            a[ctx].Should().Be(7);
        }

        [Test]
        public void Default_matches_production_defaults()
        {
            // Production: SyncPeerPool builds (headers: 1, others: AllocationSlots) — default 2.
            AllocationAllowances d = AllocationAllowances.Default;
            d.Headers.Should().Be(1);
            d.Bodies.Should().Be(2);
            d.Receipts.Should().Be(2);
            d.State.Should().Be(2);
            d.Snap.Should().Be(2);
            d.ForwardHeader.Should().Be(2);
        }
    }
}
