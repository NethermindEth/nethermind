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
    }

    [TestCaseSource(nameof(CandidatesWithStake))]
    public void Slice_DifferentOrderAndStake_SortItemsAsExpected(CandidateStake[] candidatesAndStake, Address[] expectedOrder)
    {
        XdcSort.Slice(candidatesAndStake, (x, y) => x.Stake.CompareTo(y.Stake) >= 0);

        candidatesAndStake.Select(x => x.Address).Should().Equal(expectedOrder);
    }
}
