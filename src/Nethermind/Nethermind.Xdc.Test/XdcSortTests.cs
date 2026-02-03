// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Xdc.Contracts;
using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;

namespace Nethermind.Xdc.Test;

internal class XdcSortTests
{
    public static IEnumerable<TestCaseData> CandidatesWithStake()
    {
        CandidateStake[] candidatesAndStake =
            [
            new CandidateStake() { Address = TestItem.AddressA, Stake = 10_000_000},
            new CandidateStake() { Address = TestItem.AddressB, Stake = 10_000_000},
            new CandidateStake() { Address = TestItem.AddressC, Stake = 10_000_000}
            ];
        Address[] expectedOrder = [TestItem.AddressC, TestItem.AddressB, TestItem.AddressA];

        yield return new TestCaseData(candidatesAndStake, expectedOrder);

        candidatesAndStake =
            [
            new CandidateStake() { Address = TestItem.AddressA, Stake = 10_000_000},
            new CandidateStake() { Address = TestItem.AddressB, Stake = 10_000_001},
            new CandidateStake() { Address = TestItem.AddressC, Stake = 10_000_000}
            ];
        expectedOrder = [TestItem.AddressB, TestItem.AddressC, TestItem.AddressA];

        yield return new TestCaseData(candidatesAndStake, expectedOrder);

        candidatesAndStake =
            [
            new CandidateStake() { Address = TestItem.AddressA, Stake = 10_000_001},
            new CandidateStake() { Address = TestItem.AddressB, Stake = 10_000_000},
            new CandidateStake() { Address = TestItem.AddressC, Stake = 10_000_001}
            ];
        expectedOrder = [TestItem.AddressC, TestItem.AddressA, TestItem.AddressB];

        yield return new TestCaseData(candidatesAndStake, expectedOrder);

        candidatesAndStake =
            [
            new CandidateStake() { Address = TestItem.AddressB, Stake = 10_000_000},
            new CandidateStake() { Address = TestItem.AddressC, Stake = 10_000_000},
            new CandidateStake() { Address = TestItem.AddressA, Stake = 10_000_000}
            ];
        expectedOrder = [TestItem.AddressA, TestItem.AddressC, TestItem.AddressB];

        yield return new TestCaseData(candidatesAndStake, expectedOrder);
        // Test with 20 items with same stake to verify unstable sort behavior
        candidatesAndStake = Enumerable.Range(0, 20)
            .Select(i => new CandidateStake()
            {
                Address = new Address($"0x{i:D40}"),
                Stake = 10_000_000
            })
            .ToArray();
        
        // Sort is deterministic but not stable: equal elements are reordered from original positions
        expectedOrder = new[] { 5, 4, 3, 2, 1, 12, 11, 19, 17, 15, 13, 6, 14, 7, 16, 8, 18, 9, 0, 10 }
            .Select(i => new Address($"0x{i:D40}"))
            .ToArray();
        
        yield return new TestCaseData(candidatesAndStake, expectedOrder);
    }

    [TestCaseSource(nameof(CandidatesWithStake))]
    public void Slice_DifferentOrderAndStake_SortItemsAsExpected(CandidateStake[] candidatesAndStake, Address[] expectedOrder)
    {
        XdcSort.Slice(candidatesAndStake, (x, y) => x.Stake.CompareTo(y.Stake) >= 0);

        candidatesAndStake.Select(x => x.Address).Should().Equal(expectedOrder);
    }
}
