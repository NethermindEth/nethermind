// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Autofac;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Evm;

namespace Nethermind.Consensus.Tracing;

#pragma warning disable NETH003 // Build variant: excluded from the zkEVM build, which does no tracing
/// <summary>Installs prefix replay only in explicitly compatible RPC processing environments.</summary>
public sealed class TransactionTraceModule(IEnumerable<IBlockValidationModule> validationModules) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder.AddScoped<GethStyleTracer.TraceCallRequestState>()
            .AddDecorator<IBlockhashProvider, TraceCallBlockhashProvider>();

        IBlockValidationModule[] modules = validationModules.ToArray();
        if (modules.Length == 0 || !modules.All(static module => module.SupportsTransactionTracePrefix)) return;

        builder.AddScoped<IBlockProcessor, TransactionTraceBlockProcessor>()
            .AddScoped<TransactionTraceExecutor>();
    }
}
