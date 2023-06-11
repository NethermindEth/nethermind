// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing.ParityStyle;
using Nethermind.Int256;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class TransactionProcessorTraceTest : VirtualMachineTestsBase
{
    protected override long BlockNumber => MainnetSpecProvider.GrayGlacierBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.ShanghaiBlockTimestamp;

    [TestCase(21000)]
    [TestCase(50000)]
    public void Trace_should_not_charge_gas(long gasLimit)
    {
        (Block block, Transaction transaction) = PrepareTx(BlockNumber, gasLimit);
        ParityLikeTxTracer tracer = new(block, transaction, ParityTraceTypes.All);
        _processor.Trace(transaction, block.Header, tracer);
        var senderBalance = tracer.BuildResult().StateChanges[TestItem.AddressA].Balance;
        (senderBalance.Before - senderBalance.After).Should().Be(transaction.Value);
    }
}
