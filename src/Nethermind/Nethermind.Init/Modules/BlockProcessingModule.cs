// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Init.Modules;

public class BlockProcessingModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder
            .AddScoped<ITransactionProcessor, TransactionProcessor>()
            .AddScoped<ICodeInfoRepository, CodeInfoRepository>()
            .AddScoped<IVirtualMachine, VirtualMachine>()
            .AddScoped<IBlockhashProvider, BlockhashProvider>()
            .AddSingleton<IReadOnlyTxProcessingEnvFactory, AutoReadOnlyTxProcessingEnvFactory>()
            ;
    }
}
