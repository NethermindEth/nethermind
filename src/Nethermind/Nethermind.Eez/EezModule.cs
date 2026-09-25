// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.TxPool;

namespace Nethermind.Eez;

public class EezModule : Module
{
    internal static readonly ExecutionRequestsOptions ExecutionRequestsOptions = new()
    {
        CodelessRequestContracts = CodelessRequestContractBehavior.ProduceNoRequests,
    };

    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            .AddDecorator<ISpecProvider, EezSpecProvider>()
            .AddScoped<ITransactionProcessor, EezTransactionProcessor>()
            .AddSingleton<ITransactionProcessorFactory, EezTransactionProcessorFactory>()
            .AddSingleton(ExecutionRequestsOptions)
            .AddSingleton<IBlockValidator, EezBlockValidator>()
            .AddKeyedSingleton<ITxValidator>(ITxValidator.SpecChangeTxValidatorKey,
                static ctx => new EezSpecChangeTxValidator(ctx.Resolve<ISpecProvider>().ChainId));
    }
}
