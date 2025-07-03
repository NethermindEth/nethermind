// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;

namespace Nethermind.Facade.Simulate;

public sealed class SimulateTransactionProcessor(
    ISpecProvider? specProvider,
    IWorldState? worldState,
    IVirtualMachine? virtualMachine,
    ICodeInfoRepository? codeInfoRepository,
    ILogManager? logManager,
    bool validate)
    : TransactionProcessorBase(specProvider, worldState, virtualMachine, codeInfoRepository, logManager)
{
    protected override bool ShouldValidate(ExecutionOptions opts) => true;

    protected override TransactionResult Execute(Transaction tx, ITxTracer tracer, ExecutionOptions opts)
    {
        if (!validate)
        {
            opts |= ExecutionOptions.SkipValidation;
        }

        return base.Execute(tx, tracer, opts);
    }
}

public class SimulateTransactionProcessorFactory : ISimulateTransactionProcessorFactory
{
    private SimulateTransactionProcessorFactory() { }
    public static readonly SimulateTransactionProcessorFactory Instance = new();

    public ITransactionProcessor CreateTransactionProcessor(
        ISpecProvider specProvider,
        IWorldState stateProvider,
        IVirtualMachine virtualMachine,
        ICodeInfoRepository codeInfoRepository,
        ILogManager? logManager,
        bool validate)
    {
        return new SimulateTransactionProcessor(specProvider, stateProvider, virtualMachine, codeInfoRepository, logManager, validate);
    }
}
