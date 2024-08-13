// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Facade.Simulate;

public class SimulateTransactionProcessor(
    ISpecProvider? specProvider,
    IWorldState? worldState,
    IVirtualMachine? virtualMachine,
    ICodeInfoRepository? codeInfoRepository,
    ILogManager? logManager,
    bool validate)
    : TransactionProcessor(specProvider, worldState, virtualMachine, codeInfoRepository, logManager), ITransactionProcessor
{
    public UInt256? BlobBaseFee { get; set; }

    protected override bool ShallValidate(ExecutionOptions opts)
    {
        return base.ShallValidate(opts) || opts.HasFlag(ExecutionOptions.Simulation);
    }

    protected override TransactionResult Execute(Transaction tx, in BlockExecutionContext blCtx, ITxTracer tracer, ExecutionOptions opts)
    {
        if (!validate)
        {
            opts |= ExecutionOptions.NoValidation;
        }
        opts |= ExecutionOptions.Simulation;

        if (BlobBaseFee is not null)
        {
            var blockBlCtx = new BlockExecutionContext(blCtx.Header, BlobBaseFee.Value);
            return base.Execute(tx, in blockBlCtx, tracer, opts);
        }
        else
        {
            return base.Execute(tx, in blCtx, tracer, opts);
        }

    }
}
