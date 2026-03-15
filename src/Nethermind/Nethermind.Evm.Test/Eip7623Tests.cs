// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core;
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
    public void empty_transaction_standard_equals_floor()
    {
        // No data, no access list -> 0 tokens; floor = standard = 21000
        var transaction = new Transaction { To = Address.Zero };
        EthereumIntrinsicGas cost = IntrinsicGasCalculator.Calculate(transaction, Spec);
        cost.Should().Be(new EthereumIntrinsicGas(
            Standard: GasCostOf.Transaction,
            FloorGas: GasCostOf.Transaction));
    }

    [Test]
    public void contract_creation_no_data_standard_wins()
    {
        // To = null -> TxCreate (+32000), no data -> 0 tokens; standard = 53000, floor = 21000
        var transaction = new Transaction();
        EthereumIntrinsicGas cost = IntrinsicGasCalculator.Calculate(transaction, Spec);
        cost.Should().Be(new EthereumIntrinsicGas(
            Standard: GasCostOf.Transaction + GasCostOf.TxCreate,
            FloorGas: GasCostOf.Transaction));
    }

    [Test]
    public void contract_creation_with_calldata_standard_wins()
    {
        // 5 non-zero bytes; standard = 21000 + 32000 + 5*16 + EIP-3860 initcode(1 word * 2) = 53082
        // tokens = 5*4 = 20; floor = 21000 + 20*10 = 21200 -> standard wins
        var transaction = new Transaction { Data = new byte[] { 1, 2, 3, 4, 5 } };
        EthereumIntrinsicGas cost = IntrinsicGasCalculator.Calculate(transaction, Spec);
        cost.Should().Be(new EthereumIntrinsicGas(
            Standard: GasCostOf.Transaction + GasCostOf.TxCreate + 5 * GasCostOf.TxDataNonZeroEip2028 + GasCostOf.InitCodeWord,
            FloorGas: GasCostOf.Transaction + GasCostOf.TotalCostFloorPerTokenEip7623 * (5 * 4)));
    }

    [Test]
    public void mixed_calldata_counts_tokens_correctly()
    {
        // 3 zero bytes + 2 non-zero bytes; standard = 21000 + 3*4 + 2*16 = 21044
        // tokens = 3*1 + 2*4 = 11; floor = 21000 + 11*10 = 21110
        var transaction = new Transaction { Data = new byte[] { 0, 0, 0, 1, 2 }, To = Address.Zero };
        EthereumIntrinsicGas cost = IntrinsicGasCalculator.Calculate(transaction, Spec);
        cost.Should().Be(new EthereumIntrinsicGas(
            Standard: GasCostOf.Transaction + 3 * GasCostOf.TxDataZero + 2 * GasCostOf.TxDataNonZeroEip2028,
            FloorGas: GasCostOf.Transaction + GasCostOf.TotalCostFloorPerTokenEip7623 * 11));
    }

    [Test]
    public void zero_heavy_calldata_floor_exceeds_standard()
    {
        // 100 zero bytes; standard = 21000 + 100*4 = 21400; floor = 21000 + 100*10 = 22000
        var transaction = new Transaction { Data = new byte[100], To = Address.Zero };
        EthereumIntrinsicGas cost = IntrinsicGasCalculator.Calculate(transaction, Spec);
        cost.Should().Be(new EthereumIntrinsicGas(
            Standard: GasCostOf.Transaction + 100 * GasCostOf.TxDataZero,
            FloorGas: GasCostOf.Transaction + GasCostOf.TotalCostFloorPerTokenEip7623 * 100));
    }

    [Test]
    public void floor_not_applied_before_eip7623()
    {
        // London predates EIP-7623 -> FloorGas = 0, MinimalGas = Standard
        var transaction = new Transaction { Data = new byte[] { 0 }, To = Address.Zero };
        EthereumIntrinsicGas cost = IntrinsicGasCalculator.Calculate(transaction, London.Instance);
        cost.Should().Be(new EthereumIntrinsicGas(
            Standard: GasCostOf.Transaction + GasCostOf.TxDataZero,
            FloorGas: 0));
    }
}
