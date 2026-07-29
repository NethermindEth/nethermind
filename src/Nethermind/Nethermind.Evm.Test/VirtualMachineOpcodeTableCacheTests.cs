// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class VirtualMachineOpcodeTableCacheTests : VirtualMachineTestsBase
{
    private sealed class TransientSpec(IReleaseSpec spec) : ReleaseSpecDecorator(spec);

    [Test]
    public void Opcode_table_cache_does_not_root_transient_specs()
    {
        WeakReference specReference = ExecuteWithTransientSpec();

        for (int i = 0; i < 5 && specReference.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.That(specReference.IsAlive, Is.False,
            "The process-wide opcode-table cache must not keep transient specs alive; " +
            "eth_simulateV1 wraps the spec per simulated block (WithoutEip3607), so strong keys grow without bound.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private WeakReference ExecuteWithTransientSpec()
    {
        (Block block, Transaction transaction) = PrepareTx(Activation, 100000UL, [(byte)Instruction.STOP]);
        IReleaseSpec transientSpec = new TransientSpec(SpecProvider.GetSpec(block.Header));

        _processor.Execute(transaction, new BlockExecutionContext(block.Header, transientSpec), NullTxTracer.Instance);

        // The VM retains the last block execution context; overwrite it so the opcode-table
        // cache is the only remaining candidate root for the transient spec.
        _processor.SetBlockExecutionContext(block.Header);

        return new WeakReference(transientSpec);
    }
}
