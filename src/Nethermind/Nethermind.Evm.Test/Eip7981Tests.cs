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
/// </summary>
[TestFixture]
public class Eip7981Tests
{
    private static readonly IReleaseSpec Spec = new OverridableReleaseSpec(Amsterdam.Instance) { IsEip7976Enabled = true, IsEip7981Enabled = true };
    private static long FloorPerToken => Spec.GasCosts.TotalCostFloorPerToken;

    private static IEnumerable<TestCaseData> AccessListOnlyCases()
    {
        // 20 bytes × 4 = 80 tokens
        yield return new TestCaseData(
            new AccessList.Builder().AddAddress(Address.Zero).Build(), 80L
        ).SetName("Single zero address: 80 tokens");

        yield return new TestCaseData(
            new AccessList.Builder().AddAddress(new Address("0xAB000000000000000000000000000000000000CD")).Build(), 80L
        ).SetName("Address with non-zero bytes: 80 tokens");

        // Address (80) + storage key (32 * 4 = 128) = 208 tokens
        yield return new TestCaseData(
            new AccessList.Builder().AddAddress(Address.Zero).AddStorage(UInt256.Zero).Build(), 208L
        ).SetName("Address + zero storage key: 208 tokens");

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

    private static IEnumerable<TestCaseData> CalldataWithAccessListCases()
    {
        // 1 zero byte + 1 address: standard wins
        // Standard = 21000 + 4 + 2400 + 80*16 = 24684
        // Floor = 21000 + (4 + 80)*16 = 22344
        yield return new TestCaseData(new byte[] { 0 }, 1, 0, 24684L, 22344L)
            .SetName("1 zero byte + 1 address: standard wins");

        // 40 zero bytes + 1 address: exact tie (floor == standard)
        // Standard = 21000 + 40*4 + 2400 + 1280 = 24840
        // Floor = 21000 + (160 + 80)*16 = 24840
        yield return new TestCaseData(new byte[40], 1, 0, 24840L, 24840L)
            .SetName("40 zero bytes + 1 address: exact tie");

        // 41 zero bytes + 1 address: floor wins by 60 (smallest count)
        // Standard = 21000 + 41*4 + 2400 + 1280 = 24844
        // Floor = 21000 + (164 + 80)*16 = 24904
        yield return new TestCaseData(new byte[41], 1, 0, 24844L, 24904L)
            .SetName("41 zero bytes + 1 address: floor wins by 60");

        // 100 zero bytes + 1 address + 1 key: floor dominates
        // Standard = 21000 + 400 + 2400 + 1900 + (80+128)*16 = 29028
        // Floor = 21000 + (400 + 80 + 128)*16 = 30728
        yield return new TestCaseData(new byte[100], 1, 1, 29028L, 30728L)
            .SetName("100 zero bytes + 1 address + 1 key: floor dominates");
    }

    [TestCaseSource(nameof(CalldataWithAccessListCases))]
    public void Calldata_with_access_list_floor_pricing(byte[] data, int addressCount, int storageKeyCount, long expectedStandard, long expectedFloor)
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
        cost.Should().Be(new EthereumIntrinsicGas(Standard: expectedStandard, FloorGas: expectedFloor));
    }
}
