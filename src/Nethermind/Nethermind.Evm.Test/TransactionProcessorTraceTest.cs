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

    [Test]
    public void Traces_gas_fess_properly()
    {
        (Block block, Transaction transaction) = PrepareTx(BlockNumber, 21000);
        ParityLikeTxTracer tracer = new(block, transaction, ParityTraceTypes.All);
        _processor.Trace(transaction, block.Header, tracer);
        var senderBalance = tracer.BuildResult().StateChanges[TestItem.AddressA].Balance;
        (senderBalance.Before - senderBalance.After).Should().Be((UInt256)21000 + transaction.Value);
    }
}
