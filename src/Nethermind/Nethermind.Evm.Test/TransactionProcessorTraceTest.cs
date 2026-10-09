// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Reflection;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Specs;
using NSubstitute;
using NUnit.Framework;
using Nethermind.Int256;

namespace Nethermind.Evm.Test;

public class TransactionProcessorTraceTest : VirtualMachineTestsBase
{
    protected override ulong BlockNumber => MainnetSpecProvider.GrayGlacierBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.ShanghaiBlockTimestamp;

    [Test]
    public void Trace_should_not_charge_gas([Values(21000UL, 50000UL)] ulong gasLimit)
    {
        (Block block, Transaction transaction) = PrepareTx(BlockNumber, gasLimit, gasPrice: 0);
        ParityLikeTxTracer tracer = new(block, transaction, ParityTraceTypes.All);
        _processor.Trace(transaction, new BlockExecutionContext(block.Header, Spec), tracer);
        ParityStateChange<UInt256?> senderBalance = tracer.BuildResult().StateChanges[TestItem.AddressA].Balance;
        Assert.That((senderBalance.Before - senderBalance.After), Is.EqualTo((UInt256)transaction.Value));
    }

    [Test]
    public void Simple_transfer_reads_each_tracer_flag_at_most_once([Values] bool tracing)
    {
        (Block block, Transaction transaction) = PrepareTx(BlockNumber, 21000UL);
        ITxTracer tracer = Substitute.For<ITxTracer>();
        foreach (PropertyInfo flag in typeof(ITxTracer).GetProperties().Where(static p => p.PropertyType == typeof(bool)))
        {
            flag.GetValue(tracer).Returns(tracing);
        }
        tracer.ClearReceivedCalls();

        Assert.That(_processor.Execute(transaction, new BlockExecutionContext(block.Header, Spec), tracer).TransactionExecuted, Is.True);

        string[] repeatedFlags = tracer.ReceivedCalls()
            .Select(static call => call.GetMethodInfo().Name)
            .Where(static name => name.StartsWith("get_Is"))
            .GroupBy(static name => name)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .ToArray();
        Assert.That(repeatedFlags, Is.Empty);
    }
}
