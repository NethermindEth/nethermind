// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Reflection;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// Drives each EIP-8037 state-gas invariant guard with a corrupted <see cref="EthereumGasPolicy"/>. A violation is
/// unreachable via valid input and the processor is sealed, so the protected refund methods are invoked by reflection.
/// </summary>
[TestFixture]
public class Eip8037StateGasInvariantTests : VirtualMachineTestsBase
{
    protected override ulong BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.AmsterdamBlockTimestamp;

    private const ulong GasLimit = 100_000;

    [Test]
    public void RefundOnFail_flags_negative_block_state_gas()
    {
        EthereumGasPolicy corrupted = new() { StateGasUsed = -1 };
        Assert.That(Refund("RefundOnFail", Tx(), corrupted, 0UL).StateGasInvariantError, Is.Not.Null);
    }

    [Test]
    public void RefundOnContractCollision_flags_negative_block_state_gas()
    {
        EthereumGasPolicy corrupted = new() { StateGasUsed = -1 };
        Assert.That(Refund("RefundOnContractCollision", Tx(), corrupted, 0UL).StateGasInvariantError, Is.Not.Null);
    }

    [Test]
    public void RefundOnTopLevelHalt_flags_reservoir_exceeding_gas_limit()
    {
        EthereumGasPolicy corrupted = new() { StateReservoir = (long)GasLimit + 1 };
        Assert.That(Refund("RefundOnTopLevelHalt", Tx(), corrupted, 0UL, 0UL).StateGasInvariantError, Is.Not.Null);
    }

    [Test]
    public void RefundOnTopLevelHalt_flags_state_gas_exceeding_pre_refund_gas()
    {
        EthereumGasPolicy corrupted = new() { StateGasUsed = (long)GasLimit + 1 };
        Assert.That(Refund("RefundOnTopLevelHalt", Tx(), corrupted, 0UL, 0UL).StateGasInvariantError, Is.Not.Null);
    }

    [Test]
    public void RefundOnTopLevelHalt_exempts_system_transaction_from_state_gas_check()
    {
        // System txs are exempt from the halt-path state-gas check.
        EthereumGasPolicy corrupted = new() { StateGasUsed = (long)GasLimit + 1 };
        Assert.That(Refund("RefundOnTopLevelHalt", SystemTx(), corrupted, 0UL, 0UL).StateGasInvariantError, Is.Null);
    }

    private Transaction Tx() =>
        Build.A.Transaction.WithGasLimit(GasLimit).WithSenderAddress(TestItem.AddressA).TestObject;

    private Transaction SystemTx() =>
        Build.A.Transaction.WithGasLimit(GasLimit).WithSenderAddress(Address.SystemUser).TestObject;

    private GasConsumed Refund(string methodName, Transaction tx, EthereumGasPolicy gas, params object[] tail)
    {
        MethodInfo method = typeof(TransactionProcessorBase<EthereumGasPolicy>)
            .GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!;
        object[] args = [tx, Spec, ExecutionOptions.Commit, gas, UInt256.Zero, default(EthereumGasPolicy), .. tail];
        try
        {
            return (GasConsumed)method.Invoke(_processor, args)!;
        }
        catch (TargetInvocationException e)
        {
            throw e.InnerException!;
        }
    }
}
