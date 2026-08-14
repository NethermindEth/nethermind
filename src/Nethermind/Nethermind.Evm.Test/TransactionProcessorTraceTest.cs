// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Specs;
using NUnit.Framework;
using Nethermind.Int256;

namespace Nethermind.Evm.Test;

public class TransactionProcessorTraceTest : VirtualMachineTestsBase
{
    protected override ulong BlockNumber => MainnetSpecProvider.GrayGlacierBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.ShanghaiBlockTimestamp;

    [TestCase(21000UL)]
    [TestCase(50000UL)]
    public void Trace_should_not_charge_gas(ulong gasLimit)
    {
        (Block block, Transaction transaction) = PrepareTx(BlockNumber, gasLimit, gasPrice: 0);
        ParityLikeTxTracer tracer = new(block, transaction, ParityTraceTypes.All);
        _processor.Trace(transaction, new BlockExecutionContext(block.Header, Spec), tracer);
        ParityStateChange<UInt256?> senderBalance = tracer.BuildResult().StateChanges[TestItem.AddressA].Balance;
        Assert.That((senderBalance.Before - senderBalance.After), Is.EqualTo((UInt256)transaction.Value));
    }
}
