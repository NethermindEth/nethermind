// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Reflection;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Test;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// Drives each EIP-8037 state-gas invariant guard with a corrupted <see cref="EthereumGasPolicy"/> — a violation is
/// unreachable via valid input and the processor is sealed, so the protected refund methods are invoked by reflection.
/// </summary>
[TestFixture]
public class Eip8037StateGasInvariantTests : VirtualMachineTestsBase
{
    protected override ulong BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.AmsterdamBlockTimestamp;

    private const ulong GasLimit = 100_000;

    private static IEnumerable<TestCaseData> RefundGuards()
    {
        yield return new TestCaseData("RefundOnFail", new EthereumGasPolicy { StateGasUsed = -1 }, new object[] { 0UL })
            .SetName("RefundOnFail_flags_negative_block_state_gas");
        yield return new TestCaseData("RefundOnContractCollision", new EthereumGasPolicy { StateGasUsed = -1 }, new object[] { 0UL })
            .SetName("RefundOnContractCollision_flags_negative_block_state_gas");
        yield return new TestCaseData("RefundOnTopLevelHalt", new EthereumGasPolicy { StateReservoir = (long)GasLimit + 1 }, new object[] { 0UL, 0UL })
            .SetName("RefundOnTopLevelHalt_flags_reservoir_exceeding_gas_limit");
        yield return new TestCaseData("RefundOnTopLevelHalt", new EthereumGasPolicy { StateGasUsed = (long)GasLimit + 1 }, new object[] { 0UL, 0UL })
            .SetName("RefundOnTopLevelHalt_flags_state_gas_exceeding_pre_refund_gas");
        yield return new TestCaseData("RefundOnTopLevelHalt", default(EthereumGasPolicy), new object[] { 0UL, GasLimit + 1 })
            .SetName("RefundOnTopLevelHalt_flags_refund_exceeding_pre_refund_gas");
    }

    [TestCaseSource(nameof(RefundGuards))]
    public void Refund_path_flags_state_gas_invariant(string method, EthereumGasPolicy corrupted, object[] tail)
    {
        object[] args = [Tx(), new OverridableReleaseSpec(Spec) { IsEip3298Enabled = true }, ExecutionOptions.Commit, corrupted, UInt256.Zero, default(EthereumGasPolicy), .. tail];
        RefundResult result = Invoke<RefundResult>(method, args);
        Assert.That(result.Result.Error, Is.EqualTo(TransactionResult.ErrorType.StateGasInvariantViolated));
    }

    [Test]
    public void RefundOnTopLevelHalt_exempts_system_transaction()
    {
        // System calls legitimately run with a state reservoir the halt check would otherwise reject.
        object[] args = [SystemTx(), Spec, ExecutionOptions.Commit, new EthereumGasPolicy { StateGasUsed = (long)GasLimit + 1 }, UInt256.Zero, default(EthereumGasPolicy), 0UL, 0UL];
        Assert.That((bool)Invoke<RefundResult>("RefundOnTopLevelHalt", args).Result, Is.True);
    }

    private Transaction Tx() => Build.A.Transaction.WithGasLimit(GasLimit).WithSenderAddress(TestItem.AddressA).TestObject;
    private Transaction SystemTx() => Build.A.Transaction.WithGasLimit(GasLimit).WithSenderAddress(Address.SystemUser).TestObject;

    private T Invoke<T>(string methodName, object[] args)
    {
        MethodInfo method = typeof(TransactionProcessorBase<EthereumGasPolicy>).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(TransactionProcessorBase<EthereumGasPolicy>), methodName);
        try
        {
            return (T)method.Invoke(_processor, args)!;
        }
        catch (TargetInvocationException e)
        {
            throw e.InnerException!;
        }
    }
}
