// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.State;

namespace Nethermind.Consensus.Producers
{
    public class BlockProducerEnvFactory(
        ILifetimeScope rootLifetime,
        IWorldStateManager worldStateManager,
        IBlockProducerTxSourceFactory txSourceFactory
    ) : GlobalWorldStateBlockProducerEnvFactory(rootLifetime, worldStateManager, txSourceFactory)
    {
        protected override ContainerBuilder ConfigureBuilder(ContainerBuilder builder) =>
            base.ConfigureBuilder(builder)
                .AddScoped<IReceiptStorage>(NullReceiptStorage.Instance)
                .AddScoped(BlockchainProcessor.Options.NoReceipts);

        protected override IWorldStateScopeProvider CreateWorldState() => WorldStateManager.CreateResettableWorldState();
    }
}
