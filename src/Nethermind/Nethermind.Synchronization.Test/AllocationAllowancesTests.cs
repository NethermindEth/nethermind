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
            AllocationAllowances a = AllocationAllowances.Default;
            a[ctx].Should().Be(1, "Default constructs with all-ones");

            a[ctx] = 7;
            a[ctx].Should().Be(7);
        }
    }
}
