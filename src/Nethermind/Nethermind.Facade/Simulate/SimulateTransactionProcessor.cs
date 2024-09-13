// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Facade.Simulate;

public sealed class SimulateTransactionProcessor(
    ISpecProvider? specProvider,
    IWorldState? worldState,
    IVirtualMachine? virtualMachine,
    ICodeInfoRepository? codeInfoRepository,
    ILogManager? logManager,
    bool validate)
    : TransactionProcessorBase(specProvider, worldState, virtualMachine, codeInfoRepository, logManager), ITransactionProcessor
{
    protected override TransactionResult Execute(Transaction tx, in BlockExecutionContext blCtx, ITxTracer tracer, ExecutionOptions opts)
    {
        if (!validate)
        {
            opts |= ExecutionOptions.NoValidation;
        }

        return base.Execute(tx, in blCtx, tracer, opts);
    }
}
