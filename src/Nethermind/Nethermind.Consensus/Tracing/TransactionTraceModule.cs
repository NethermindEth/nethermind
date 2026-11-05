// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Autofac;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Specs;

namespace Nethermind.Consensus.Tracing;

/// <summary>Installs prefix replay only in explicitly compatible RPC processing environments.</summary>
public sealed class TransactionTraceModule(IEnumerable<IBlockValidationModule> validationModules) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        IBlockValidationModule[] modules = validationModules.ToArray();
        if (modules.Length == 0 || !modules.All(static module => module.SupportsTransactionTracePrefix)) return;

        builder.AddScoped<IBlockProcessor, TransactionTraceBlockProcessor>()
            .AddScoped<TransactionTraceExecutor>()
            // Only this scope runs the executor that recovers the senders of a bounded replay; a decorator wraps
            // the processor and still reaches it. Every other scope keeps the full recovery up front.
            .Add<BlockchainProcessorFacade>(static ctx => new BlockchainProcessorFacade(
                ctx.Resolve<IBlockProcessor>(),
                ctx.Resolve<ISpecProvider>(),
                ctx.Resolve<IReadOnlyList<IBlockPreprocessorStep>>(),
                executorRecoversTraceSenders: true));
    }
}
