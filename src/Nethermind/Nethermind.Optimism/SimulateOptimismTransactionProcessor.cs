// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Facade.Simulate;

namespace Nethermind.Optimism;

public sealed class SimulateOptimismTransactionProcessor(
    ISpecProvider specProvider,
    IWorldState worldState,
    IVirtualMachine virtualMachine,
    IL1CostHelper l1CostHelper,
    IOptimismSpecHelper opSpecHelper,
    ICodeInfoRepository? codeInfoRepository,
    ILogManager logManager,
    bool validate)
    : OptimismTransactionProcessor(
        specProvider,
        worldState,
        virtualMachine,
        logManager,
        l1CostHelper,
        opSpecHelper,
        codeInfoRepository)
{
    private readonly IL1CostHelper _l1CostHelper = l1CostHelper;
    private readonly IOptimismSpecHelper _opSpecHelper = opSpecHelper;
    protected override bool ShouldValidate(ExecutionOptions opts) => true;

    public override ITransactionProcessor WithNewStateProvider(IWorldState worldState)
    {
        return new SimulateOptimismTransactionProcessor(SpecProvider, worldState, VirtualMachine, _l1CostHelper,
            _opSpecHelper, CodeInfoRepository, LogManager, validate);
    }

    protected override TransactionResult Execute(Transaction tx, in BlockExecutionContext blCtx, ITxTracer tracer, ExecutionOptions opts)
    {
        if (!validate)
        {
            opts |= ExecutionOptions.SkipValidation;
        }

        return base.Execute(tx, in blCtx, tracer, opts);
    }
}

public sealed class SimulateOptimismTransactionProcessorFactory(
    IL1CostHelper l1CostHelper,
    IOptimismSpecHelper opSpecHelper
) : ISimulateTransactionProcessorFactory
{
    public ITransactionProcessor CreateTransactionProcessor(
        ISpecProvider specProvider,
        IWorldState worldState,
        SimulateVirtualMachine virtualMachine,
        OverridableCodeInfoRepository codeInfoRepository,
        ILogManager? logManager,
        bool validate)
    {
        return new SimulateOptimismTransactionProcessor(
            specProvider,
            worldState,
            virtualMachine,
            l1CostHelper,
            opSpecHelper,
            codeInfoRepository,
            logManager!,
            validate);
    }
}
