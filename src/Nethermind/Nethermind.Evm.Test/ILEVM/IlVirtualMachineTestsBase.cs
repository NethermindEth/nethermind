// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Evm.Test.ILEVM;


public abstract class IlVirtualMachineTestsBase(bool useIlEVM) : VirtualMachineTestsBase
{
    private readonly bool _useIlEvm = useIlEVM;

    protected void Execute(Transaction transaction, Block block)
    {
        TransactionResult result = _processor.Execute(transaction, new BlockExecutionContext(block.Header, Spec),
            NullTxTracer.Instance);

        result.Success.Should().Be(true);
    }
}
