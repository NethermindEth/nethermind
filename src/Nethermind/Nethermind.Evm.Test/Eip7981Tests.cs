// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// Tests for EIP-7981: access list token floor pricing.
/// Uses OverridableReleaseSpec until pyspec fixtures include EIP-7981.
/// </summary>
[TestFixture]
public class Eip7981Tests
{
    private static readonly IReleaseSpec Spec = new OverridableReleaseSpec(Amsterdam.Instance) { IsEip7981Enabled = true };
    private static long FloorPerToken => Spec.GasCosts.TotalCostFloorPerToken;

    private static IEnumerable<TestCaseData> AccessListOnlyCases()
    {
        // Address.Zero = 20 zero bytes = 20 tokens
        yield return new TestCaseData(
            new AccessList.Builder().AddAddress(Address.Zero).Build(), 20L
        ).SetName("Single zero address: 20 tokens");

        // AB 00x18 CD -> 18 zero + 2 non-zero (2*4=8) = 26 tokens
        yield return new TestCaseData(
            new AccessList.Builder().AddAddress(new Address("0xAB000000000000000000000000000000000000CD")).Build(), 26L
        ).SetName("Address with non-zero bytes: 26 tokens");

        // Address.Zero (20) + UInt256.Zero (32) = 52 tokens
        yield return new TestCaseData(
            new AccessList.Builder().AddAddress(Address.Zero).AddStorage(UInt256.Zero).Build(), 52L
        ).SetName("Address + zero storage key: 52 tokens");

        // Address.Zero (20) + UInt256.One (31 zero + 1*4) = 55 tokens
        yield return new TestCaseData(
            new AccessList.Builder().AddAddress(Address.Zero).AddStorage(UInt256.One).Build(), 55L
        ).SetName("Address + non-zero storage key: 55 tokens");

        // Address.Zero (20) + 0x00..01 (19 zero + 1*4 = 23) = 43 tokens
        yield return new TestCaseData(
            new AccessList.Builder().AddAddress(Address.Zero).AddAddress(new Address("0x0000000000000000000000000000000000000001")).Build(), 43L
        ).SetName("Two addresses accumulate tokens: 43 tokens");
    }

    [TestCaseSource(nameof(AccessListOnlyCases))]
    public void access_list_token_pricing(AccessList accessList, long expectedTokens)
    {
        Transaction transaction = new() { To = Address.Zero, AccessList = accessList };
        EthereumIntrinsicGas cost = IntrinsicGasCalculator.Calculate(transaction, Spec);

        (int addressCount, int storageKeyCount) = accessList.Count;
        long expectedStandard = GasCostOf.Transaction
            + addressCount * GasCostOf.AccessAccountListEntry
            + storageKeyCount * GasCostOf.AccessStorageListEntry
            + FloorPerToken * expectedTokens;
        long expectedFloor = GasCostOf.Transaction + FloorPerToken * expectedTokens;

        cost.Should().Be(new EthereumIntrinsicGas(Standard: expectedStandard, FloorGas: expectedFloor));
    }

    [Test]
    public void token_floor_not_applied_before_eip7981()
    {
        // Prague: EIP-7623 enabled, EIP-7981 NOT enabled -> no token floor on access list
        AccessList accessList = new AccessList.Builder().AddAddress(Address.Zero).Build();
        Transaction transaction = new() { To = Address.Zero, AccessList = accessList };
        EthereumIntrinsicGas cost = IntrinsicGasCalculator.Calculate(transaction, Prague.Instance);
        cost.Should().Be(new EthereumIntrinsicGas(
            Standard: GasCostOf.Transaction + GasCostOf.AccessAccountListEntry,
            FloorGas: GasCostOf.Transaction));
    }

    [Test]
    public void calldata_and_access_list_both_contribute_to_floor()
    {
        // Data: [0] = 1 calldata token; Address.Zero = 20 access list tokens; total floor = 21 tokens
        AccessList accessList = new AccessList.Builder().AddAddress(Address.Zero).Build();
        Transaction transaction = new() { To = Address.Zero, Data = new byte[] { 0 }, AccessList = accessList };
        EthereumIntrinsicGas cost = IntrinsicGasCalculator.Calculate(transaction, Spec);
        cost.Should().Be(new EthereumIntrinsicGas(
            Standard: GasCostOf.Transaction + GasCostOf.TxDataZero + GasCostOf.AccessAccountListEntry + FloorPerToken * 20,
            FloorGas: GasCostOf.Transaction + FloorPerToken * 21));
    }
}
