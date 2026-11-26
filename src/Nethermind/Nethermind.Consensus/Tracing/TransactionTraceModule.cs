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
/// <remarks>The replay runs on <see cref="TransactionTraceBlockProcessor"/>, unless the chain registers its own variant
/// of its block processor under <see cref="BlockProcessorKey"/>.</remarks>
public sealed class TransactionTraceModule(IEnumerable<IBlockValidationModule> validationModules) : Module
{
    /// <summary>The key of the <see cref="BlockProcessor"/> a chain with its own block processor replays transaction
    /// prefixes with. It takes the <see cref="TransactionTraceExecutor"/> as its transactions executor and applies
    /// <see cref="TransactionTraceReplay"/>. It is keyed as a <see cref="BlockProcessor"/> rather than an
    /// <see cref="IBlockProcessor"/> so that the decorators of the latter wrap the processor once.</summary>
    public const string BlockProcessorKey = "TransactionTrace";

    protected override void Load(ContainerBuilder builder)
    {
        IBlockValidationModule[] modules = validationModules.ToArray();
        if (modules.Length == 0 || !modules.All(static module => module.SupportsTransactionTracePrefix)) return;

        builder.AddScoped<TransactionTraceBlockProcessor>()
            .AddScoped<IBlockProcessor>(static ctx =>
                ctx.ResolveOptionalKeyed<BlockProcessor>(BlockProcessorKey) ?? ctx.Resolve<TransactionTraceBlockProcessor>())
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
