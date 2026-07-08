// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
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
/// </summary>
[TestFixture]
public class Eip7981Tests
{
    private static readonly IReleaseSpec Spec = new OverridableReleaseSpec(Amsterdam.Instance) { IsEip7976Enabled = true, IsEip7981Enabled = true };
    private static ulong FloorPerToken => Spec.GasCosts.TotalCostFloorPerToken;

    private static IEnumerable<TestCaseData> AccessListOnlyCases()
    {
        // 20 bytes × 4 = 80 tokens
        yield return new TestCaseData(
            new AccessList.Builder().AddAddress(Address.Zero).Build(), 80UL
        ).SetName("Single zero address: 80 tokens");

        yield return new TestCaseData(
            new AccessList.Builder().AddAddress(new Address("0xAB000000000000000000000000000000000000CD")).Build(), 80UL
        ).SetName("Address with non-zero bytes: 80 tokens");

        // Address (80) + storage key (32 * 4 = 128) = 208 tokens
        yield return new TestCaseData(
            new AccessList.Builder().AddAddress(Address.Zero).AddStorage(UInt256.Zero).Build(), 208UL
        ).SetName("Address + zero storage key: 208 tokens");

        yield return new TestCaseData(
            new AccessList.Builder().AddAddress(Address.Zero).AddStorage(UInt256.One).Build(), 208UL
        ).SetName("Address + non-zero storage key: 208 tokens");

        // Two addresses: 2 * 80 = 160 tokens
        yield return new TestCaseData(
            new AccessList.Builder().AddAddress(Address.Zero).AddAddress(new Address("0x0000000000000000000000000000000000000001")).Build(), 160UL
        ).SetName("Two addresses: 160 tokens");

        // 1 address (80) + 3 keys (3 × 128 = 384) = 464 tokens
        yield return new TestCaseData(
            new AccessList.Builder()
                .AddAddress(Address.Zero)
                .AddStorage(UInt256.Zero)
                .AddStorage(UInt256.One)
                .AddStorage(new UInt256(255))
                .Build(), 464UL
        ).SetName("Address + 3 storage keys: 464 tokens");

        // Empty access list: no addresses, no keys → 0 tokens
        yield return new TestCaseData(
            new AccessList.Builder().Build(), 0UL
        ).SetName("Empty access list: 0 tokens");
    }

    [TestCaseSource(nameof(AccessListOnlyCases))]
    public void Access_list_token_pricing(AccessList accessList, ulong expectedTokens)
    {
        Transaction transaction = new() { To = Address.Zero, AccessList = accessList };
        EthereumIntrinsicGas cost = IntrinsicGasCalculator.Calculate(transaction, Spec);

        (int addressCount, int storageKeyCount) = accessList.Count;
        // Standard intrinsic: TX_BASE + recipient cold touch + per access-list entry + floor tokens;
        // the calldata floor is TX_BASE + floor tokens only (no recipient/access-entry component).
        ulong expectedStandard = GasCostOf.TransactionEip2780
            + Eip8038Constants.ColdAccountAccess
            + (ulong)addressCount * Eip8038Constants.AccessListAddressCost
            + (ulong)storageKeyCount * Eip8038Constants.AccessListStorageKeyCost
            + FloorPerToken * expectedTokens;
        ulong expectedFloor = GasCostOf.TransactionEip2780 + FloorPerToken * expectedTokens;

        Assert.That(cost, Is.EqualTo(new EthereumIntrinsicGas(Standard: expectedStandard, FloorGas: expectedFloor)));
    }

    [Test]
    public void Null_access_list_contributes_zero_tokens()
    {
        Transaction transaction = new() { To = Address.Zero, AccessList = null };
        EthereumIntrinsicGas cost = IntrinsicGasCalculator.Calculate(transaction, Spec);
        Assert.That(cost, Is.EqualTo(new EthereumIntrinsicGas(
            Standard: GasCostOf.TransactionEip2780 + Eip8038Constants.ColdAccountAccess,
            FloorGas: GasCostOf.TransactionEip2780)));
    }

    [Test]
    public void Token_floor_not_applied_before_eip7981()
    {
        AccessList accessList = new AccessList.Builder().AddAddress(Address.Zero).Build();
        Transaction transaction = new() { To = Address.Zero, AccessList = accessList };
        EthereumIntrinsicGas cost = IntrinsicGasCalculator.Calculate(transaction, Prague.Instance);
        Assert.That(cost, Is.EqualTo(new EthereumIntrinsicGas(
            Standard: GasCostOf.Transaction + GasCostOf.AccessAccountListEntry,
            FloorGas: GasCostOf.Transaction)));
    }

    private static IEnumerable<TestCaseData> CalldataWithAccessListCases()
    {
        // standardWins: with little calldata the standard's fixed recipient/access-entry component
        // dominates the floor's 16-vs-4 per-token premium.
        yield return new TestCaseData(new byte[] { 0 }, 1, 0, true)
            .SetName("1 zero byte + 1 address: standard wins");

        // floorWins: enough calldata that the floor's per-token premium outgrows the standard's
        // fixed recipient + access-entry costs.
        yield return new TestCaseData(new byte[800], 1, 0, false)
            .SetName("800 zero bytes + 1 address: floor wins");

        yield return new TestCaseData(new byte[1000], 1, 1, false)
            .SetName("1000 zero bytes + 1 address + 1 key: floor dominates");
    }

    [TestCaseSource(nameof(CalldataWithAccessListCases))]
    public void Calldata_with_access_list_floor_pricing(byte[] data, int addressCount, int storageKeyCount, bool standardWins)
    {
        AccessList.Builder builder = new();
        for (int i = 0; i < addressCount; i++)
        {
            builder.AddAddress(new Address($"0x{i:D40}"));
            for (int j = 0; j < storageKeyCount; j++)
            {
                builder.AddStorage(new UInt256((ulong)(i * storageKeyCount + j)));
            }
        }

        AccessList accessList = builder.Build();
        Transaction transaction = new() { To = Address.Zero, Data = data, AccessList = accessList };
        EthereumIntrinsicGas cost = IntrinsicGasCalculator.Calculate(transaction, Spec);

        // The transaction is charged the larger of the standard and floor intrinsic.
        if (standardWins)
        {
            Assert.That(cost.Standard, Is.GreaterThan(cost.FloorGas));
            Assert.That(cost.MinimalGas, Is.EqualTo(cost.Standard));
        }
        else
        {
            Assert.That(cost.FloorGas, Is.GreaterThan(cost.Standard));
            Assert.That(cost.MinimalGas, Is.EqualTo(cost.FloorGas));
        }
    }

    [Test]
    public void Calldata_with_access_list_floor_equals_standard_at_exact_tie()
    {
        // Sized so the standard's fixed recipient + access-entry component exactly equals the
        // floor's per-token premium over the standard's per-byte data cost.
        AccessList accessList = new AccessList.Builder().AddAddress(Address.Zero).Build();
        Transaction transaction = new() { To = Address.Zero, Data = new byte[100], AccessList = accessList };

        EthereumIntrinsicGas cost = IntrinsicGasCalculator.Calculate(transaction, Spec);

        Assert.That(cost.Standard, Is.EqualTo(cost.FloorGas));
        Assert.That(cost.MinimalGas, Is.EqualTo(19_680UL));
    }
}
