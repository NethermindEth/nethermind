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
    private static readonly IReleaseSpec Spec = new OverridableReleaseSpec(Amsterdam.Instance) { IsEip7976Enabled = true, IsEip7981Enabled = true };
    private static long FloorPerToken => Spec.GasCosts.TotalCostFloorPerToken;
    private static long NonZeroMultiplier => Spec.GasCosts.TxDataNonZeroMultiplier;

    private static IEnumerable<TestCaseData> AccessListOnlyCases()
    {
        // Address = 20 bytes; new formula: 20 * 4 = 80 tokens (all bytes × multiplier)
        yield return new TestCaseData(
            new AccessList.Builder().AddAddress(Address.Zero).Build(), 80L
        ).SetName("Single zero address: 80 tokens");

        // Any address is 20 bytes × 4 = 80 tokens regardless of byte values
        yield return new TestCaseData(
            new AccessList.Builder().AddAddress(new Address("0xAB000000000000000000000000000000000000CD")).Build(), 80L
        ).SetName("Address with non-zero bytes: 80 tokens");

        // Address.Zero (80) + UInt256.Zero (32 * 4 = 128) = 208 tokens
        yield return new TestCaseData(
            new AccessList.Builder().AddAddress(Address.Zero).AddStorage(UInt256.Zero).Build(), 208L
        ).SetName("Address + zero storage key: 208 tokens");

        // Address.Zero (80) + UInt256.One (128) = 208 tokens (byte values don't matter)
        yield return new TestCaseData(
            new AccessList.Builder().AddAddress(Address.Zero).AddStorage(UInt256.One).Build(), 208L
        ).SetName("Address + non-zero storage key: 208 tokens");

        // Two addresses: 2 * 80 = 160 tokens
        yield return new TestCaseData(
            new AccessList.Builder().AddAddress(Address.Zero).AddAddress(new Address("0x0000000000000000000000000000000000000001")).Build(), 160L
        ).SetName("Two addresses: 160 tokens");

        // 1 address (80) + 3 keys (3 × 128 = 384) = 464 tokens
        yield return new TestCaseData(
            new AccessList.Builder()
                .AddAddress(Address.Zero)
                .AddStorage(UInt256.Zero)
                .AddStorage(UInt256.One)
                .AddStorage(new UInt256(255))
                .Build(), 464L
        ).SetName("Address + 3 storage keys: 464 tokens");

        // Empty access list: no addresses, no keys → 0 tokens
        yield return new TestCaseData(
            new AccessList.Builder().Build(), 0L
        ).SetName("Empty access list: 0 tokens");
    }

    [TestCaseSource(nameof(AccessListOnlyCases))]
    public void Access_list_token_pricing(AccessList accessList, long expectedTokens)
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
    public void Null_access_list_contributes_zero_tokens()
    {
        Transaction transaction = new() { To = Address.Zero, AccessList = null };
        EthereumIntrinsicGas cost = IntrinsicGasCalculator.Calculate(transaction, Spec);
        cost.Should().Be(new EthereumIntrinsicGas(
            Standard: GasCostOf.Transaction,
            FloorGas: GasCostOf.Transaction));
    }

    [Test]
    public void Token_floor_not_applied_before_eip7981()
    {
        AccessList accessList = new AccessList.Builder().AddAddress(Address.Zero).Build();
        Transaction transaction = new() { To = Address.Zero, AccessList = accessList };
        EthereumIntrinsicGas cost = IntrinsicGasCalculator.Calculate(transaction, Prague.Instance);
        cost.Should().Be(new EthereumIntrinsicGas(
            Standard: GasCostOf.Transaction + GasCostOf.AccessAccountListEntry,
            FloorGas: GasCostOf.Transaction));
    }

    [Test]
    public void Calldata_and_access_list_both_contribute_to_floor()
    {
        // Data: [0] under EIP-7976 floor → 1 byte * 4 (multiplier) = 4 calldata floor tokens
        // Access list: Address.Zero = 80 tokens
        // Total floor tokens = 4 + 80 = 84; floor = 21000 + 84 * 16 = 22344
        AccessList accessList = new AccessList.Builder().AddAddress(Address.Zero).Build();
        Transaction transaction = new() { To = Address.Zero, Data = new byte[] { 0 }, AccessList = accessList };
        EthereumIntrinsicGas cost = IntrinsicGasCalculator.Calculate(transaction, Spec);
        cost.Should().Be(new EthereumIntrinsicGas(
            Standard: GasCostOf.Transaction + GasCostOf.TxDataZero + GasCostOf.AccessAccountListEntry + FloorPerToken * 80,
            FloorGas: GasCostOf.Transaction + FloorPerToken * (1 * NonZeroMultiplier + 80)));
    }
}
