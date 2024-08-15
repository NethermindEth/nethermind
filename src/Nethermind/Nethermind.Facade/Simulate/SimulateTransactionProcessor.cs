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
    IVirtualMachine? virtualMachine,
    ICodeInfoRepository? codeInfoRepository,
    ILogManager? logManager,
    bool validate)
    : TransactionProcessor(specProvider, virtualMachine, codeInfoRepository, logManager), ITransactionProcessor
{
    protected override bool ShouldValidate(ExecutionOptions opts) => true;

    protected override TransactionResult Execute(IWorldState worldState, Transaction tx, in BlockExecutionContext blCtx, ITxTracer tracer, ExecutionOptions opts)
    {
        if (!validate)
        {
            opts |= ExecutionOptions.NoValidation;
        }

        return base.Execute(worldState, tx, in blCtx, tracer, opts);
    }
}
