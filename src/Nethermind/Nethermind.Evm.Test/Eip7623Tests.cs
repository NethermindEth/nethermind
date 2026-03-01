// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

[TestFixture]
public class Eip7623Tests : VirtualMachineTestsBase
{
    protected override long BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.PragueBlockTimestamp;

    [Test]
    public void non_zero_data_transaction_floor_cost_should_be_40()
    {
        var transaction = new Transaction { Data = new byte[] { 1 }, To = Address.Zero };
        EthereumIntrinsicGas cost = IntrinsicGasCalculator.Calculate(transaction, Spec);
        cost.Should().Be(new EthereumIntrinsicGas(Standard: GasCostOf.Transaction + GasCostOf.TxDataNonZeroEip2028,
            FloorGas: GasCostOf.Transaction + GasCostOf.TotalCostFloorPerTokenEip7623 * 4));
    }

    [Test]
    public void zero_data_transaction_floor_cost_should_be_10()
    {
        var transaction = new Transaction { Data = new byte[] { 0 }, To = Address.Zero };
        EthereumIntrinsicGas cost = IntrinsicGasCalculator.Calculate(transaction, Spec);
        cost.Should().Be(new EthereumIntrinsicGas(Standard: GasCostOf.Transaction + GasCostOf.TxDataZero,
            FloorGas: GasCostOf.Transaction + GasCostOf.TotalCostFloorPerTokenEip7623));
    }

    [Test]
    public void access_list_address_cost_includes_token_floor_with_eip7981()
    {
        // Address.Zero = 20 zero bytes = 20 tokens
        // access_list_cost = 2400 + 10 * 20 = 2600; floor = 21000 + 10 * 20 = 21200
        AccessList accessList = new AccessList.Builder().AddAddress(Address.Zero).Build();
        Transaction transaction = new() { To = Address.Zero, AccessList = accessList };
        EthereumIntrinsicGas cost = IntrinsicGasCalculator.Calculate(transaction, Amsterdam.Instance);
        cost.Should().Be(new EthereumIntrinsicGas(
            Standard: GasCostOf.Transaction + GasCostOf.AccessAccountListEntry + GasCostOf.TotalCostFloorPerTokenEip7623 * 20,
            FloorGas: GasCostOf.Transaction + GasCostOf.TotalCostFloorPerTokenEip7623 * 20));
    }

    [Test]
    public void access_list_token_floor_not_applied_before_eip7981()
    {
        // Prague: EIP-7623 enabled, EIP-7981 NOT enabled → no token floor on access list
        AccessList accessList = new AccessList.Builder().AddAddress(Address.Zero).Build();
        Transaction transaction = new() { To = Address.Zero, AccessList = accessList };
        EthereumIntrinsicGas cost = IntrinsicGasCalculator.Calculate(transaction, Prague.Instance);
        cost.Should().Be(new EthereumIntrinsicGas(
            Standard: GasCostOf.Transaction + GasCostOf.AccessAccountListEntry,
            FloorGas: GasCostOf.Transaction));
    }

    [Test]
    public void access_list_with_storage_key_includes_all_tokens_with_eip7981()
    {
        // Address.Zero (20 zero bytes = 20 tokens) + UInt256.Zero (32 zero bytes = 32 tokens) = 52 tokens
        // access_list_cost = 2400 + 1900 + 10 * 52 = 4820; floor = 21000 + 10 * 52 = 21520
        AccessList accessList = new AccessList.Builder()
            .AddAddress(Address.Zero)
            .AddStorage(UInt256.Zero)
            .Build();
        Transaction transaction = new() { To = Address.Zero, AccessList = accessList };
        EthereumIntrinsicGas cost = IntrinsicGasCalculator.Calculate(transaction, Amsterdam.Instance);
        cost.Should().Be(new EthereumIntrinsicGas(
            Standard: GasCostOf.Transaction + GasCostOf.AccessAccountListEntry + GasCostOf.AccessStorageListEntry + GasCostOf.TotalCostFloorPerTokenEip7623 * 52,
            FloorGas: GasCostOf.Transaction + GasCostOf.TotalCostFloorPerTokenEip7623 * 52));
    }
}
