// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.State;
using Nethermind.Taiko.BlockTransactionExecutors;
using Nethermind.Taiko.ZkGas;

namespace Nethermind.Taiko;

public class TaikoBlockProductionEnvFactory(ILifetimeScope rootLifetime, IProcessingEnvBuilder envBuilder, IWorldStateManager worldStateManager, IBlockProducerTxSourceFactory txSourceFactory) : BlockProducerEnvFactory(rootLifetime, envBuilder, worldStateManager, txSourceFactory)
{
    protected override ContainerBuilder ConfigureBuilder(ContainerBuilder builder) =>
        // Taiko does not seems to use `BlockProductionTransactionsExecutor`
        base.ConfigureBuilder(builder)
            .AddScoped<ZkGasMeterHolder>()
            .AddScoped<IBlockProcessor.IBlockTransactionsExecutor, BlockInvalidTxExecutor>();
}
