// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

[TestFixture]
public class Eip7623Tests: VirtualMachineTestsBase
{
    protected override long BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.PragueBlockTimestamp;

    [Test]
    public void non_zero_data_transaction_floor_cost_should_be_40()
    {
        var transaction = new Transaction { Data = new byte[] { 1 }, To = Address.Zero };
        var cost = IntrinsicGasCalculator.Calculate(transaction, Spec);
        cost.FloorGas.Should().Be(GasCostOf.Transaction + GasCostOf.TotalCostFloorPerTokenEip7623 * 4);
        cost.IntrinsicGas.Should().Be(GasCostOf.Transaction + GasCostOf.TxDataNonZeroEip2028);
    }

    [Test]
    public void zero_data_transaction_floor_cost_should_be_10()
    {
        var transaction = new Transaction { Data = new byte[] { 0 }, To = Address.Zero };
        var cost = IntrinsicGasCalculator.Calculate(transaction, Spec);
        cost.IntrinsicGas.Should().Be(GasCostOf.Transaction + GasCostOf.TxDataZero);
        cost.FloorGas.Should().Be(GasCostOf.Transaction + GasCostOf.TotalCostFloorPerTokenEip7623);
    }
}
