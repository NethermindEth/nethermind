// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class Eip8037Tests
{
    [TestCase(nameof(GasCostOf.CostPerStateByte), 1174)]
    [TestCase(nameof(GasCostOf.SSetState), 37568)]
    [TestCase(nameof(GasCostOf.CreateState), 131488)]
    [TestCase(nameof(GasCostOf.NewAccountState), 131488)]
    [TestCase(nameof(GasCostOf.PerAuthBaseState), 27002)]
    public void Constants_are_calculated_correctly(string name, long expected)
    {
        long actual = name switch
        {
            nameof(GasCostOf.CostPerStateByte) => GasCostOf.CostPerStateByte,
            nameof(GasCostOf.SSetState) => GasCostOf.SSetState,
            nameof(GasCostOf.CreateState) => GasCostOf.CreateState,
            nameof(GasCostOf.NewAccountState) => GasCostOf.NewAccountState,
            nameof(GasCostOf.PerAuthBaseState) => GasCostOf.PerAuthBaseState,
            _ => throw new System.ArgumentException(name)
        };
        Assert.That(actual, Is.EqualTo(expected));
    }

    [TestCase(1, 6, 1174)]
    [TestCase(32, 6, 37568)]
    [TestCase(33, 12, 38742)]
    public void Code_deposit_costs_are_split(int codeLength, long expectedRegular, long expectedState)
    {
        IReleaseSpec spec = Amsterdam.Instance;

        bool valid = CodeDepositHandler.CalculateCost(spec, codeLength, out long regularCost, out long stateCost);

        Assert.That(valid, Is.True);
        Assert.That(regularCost, Is.EqualTo(expectedRegular));
        Assert.That(stateCost, Is.EqualTo(expectedState));
    }

    [Test]
    public void State_gas_consumption_spills_to_regular_gas()
    {
        EthereumGasPolicy gas = new()
        {
            Value = 100,
            StateReservoir = 50,
            StateGasUsed = 0,
        };

        bool consumed = EthereumGasPolicy.ConsumeStateGas(ref gas, 70);

        Assert.That(consumed, Is.True);
        Assert.That(gas.StateReservoir, Is.EqualTo(0));
        Assert.That(gas.Value, Is.EqualTo(80));
        Assert.That(gas.StateGasUsed, Is.EqualTo(70));
    }

    [Test]
    public void Child_frame_gets_full_state_reservoir()
    {
        EthereumGasPolicy parent = new()
        {
            Value = 1_000,
            StateReservoir = 333,
            StateGasUsed = 50,
        };

        EthereumGasPolicy child = EthereumGasPolicy.CreateChildFrameGas(ref parent, 444);

        Assert.That(parent.Value, Is.EqualTo(1_000));
        Assert.That(parent.StateReservoir, Is.EqualTo(0));
        Assert.That(parent.StateGasUsed, Is.EqualTo(50));
        Assert.That(child.Value, Is.EqualTo(444));
        Assert.That(child.StateReservoir, Is.EqualTo(333));
        Assert.That(child.StateGasUsed, Is.EqualTo(0));
    }

    [Test]
    public void Child_frame_refund_restores_remaining_state_reservoir()
    {
        EthereumGasPolicy parent = new()
        {
            Value = 1_000,
            StateReservoir = 333,
            StateGasUsed = 50,
        };

        EthereumGasPolicy child = EthereumGasPolicy.CreateChildFrameGas(ref parent, 444);
        bool stateConsumed = EthereumGasPolicy.ConsumeStateGas(ref child, 100);
        bool regularConsumed = EthereumGasPolicy.UpdateGas(ref child, 150);

        Assert.That(stateConsumed, Is.True);
        Assert.That(regularConsumed, Is.True);

        EthereumGasPolicy.Refund(ref parent, in child);

        Assert.That(parent.Value, Is.EqualTo(1_294));
        Assert.That(parent.StateReservoir, Is.EqualTo(233));
        Assert.That(parent.StateGasUsed, Is.EqualTo(150));
    }

    [Test]
    public void State_refund_is_clamped_to_intrinsic_state_floor()
    {
        EthereumGasPolicy gas = new()
        {
            Value = 100,
            StateReservoir = 0,
            StateGasUsed = 120,
        };

        EthereumGasPolicy.RefundStateGas(ref gas, 200, stateGasFloor: 40);

        Assert.That(gas.StateReservoir, Is.EqualTo(200));
        Assert.That(gas.StateGasUsed, Is.EqualTo(0));
    }

    [Test]
    public void Exceptional_halt_preserves_state_gas()
    {
        EthereumGasPolicy parent = new()
        {
            Value = 1_000,
            StateReservoir = 500,
            StateGasUsed = 10,
        };

        EthereumGasPolicy child = EthereumGasPolicy.CreateChildFrameGas(ref parent, 600);
        Assert.That(parent.StateReservoir, Is.EqualTo(0));

        EthereumGasPolicy.ConsumeStateGas(ref child, 200);
        Assert.That(child.StateReservoir, Is.EqualTo(300));
        Assert.That(child.StateGasUsed, Is.EqualTo(200));

        EthereumGasPolicy.SetOutOfGas(ref child);
        Assert.That(child.Value, Is.EqualTo(0));
        Assert.That(child.StateReservoir, Is.EqualTo(300));

        EthereumGasPolicy.RestoreChildStateGas(ref parent, in child, 500);
        Assert.That(parent.StateReservoir, Is.EqualTo(500));
        Assert.That(parent.StateGasUsed, Is.EqualTo(10));
    }

    [Test]
    public void Revert_restores_state_gas_to_parent_reservoir()
    {
        EthereumGasPolicy parent = new()
        {
            Value = 1_000,
            StateReservoir = 400,
            StateGasUsed = 20,
        };

        EthereumGasPolicy.Consume(ref parent, 600);
        EthereumGasPolicy child = EthereumGasPolicy.CreateChildFrameGas(ref parent, 600);

        EthereumGasPolicy.UpdateGas(ref child, 100);
        EthereumGasPolicy.ConsumeStateGas(ref child, 150);

        EthereumGasPolicy.UpdateGasUp(ref parent, EthereumGasPolicy.GetRemainingGas(in child));
        EthereumGasPolicy.RestoreChildStateGas(ref parent, in child, 400);

        Assert.That(parent.Value, Is.EqualTo(900));
        Assert.That(parent.StateReservoir, Is.EqualTo(400));
        Assert.That(parent.StateGasUsed, Is.EqualTo(20));
    }

    [Test]
    public void Spent_gas_subtracts_state_reservoir()
    {
        long gasLimit = 10_000;
        EthereumGasPolicy gas = new()
        {
            Value = 3_000,
            StateReservoir = 2_000,
            StateGasUsed = 500,
        };

        long spentGas = gasLimit
            - EthereumGasPolicy.GetRemainingGas(in gas)
            - EthereumGasPolicy.GetStateReservoir(in gas);

        Assert.That(spentGas, Is.EqualTo(5_000));
    }
}
