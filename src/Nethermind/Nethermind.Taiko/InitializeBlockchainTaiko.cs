// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Autofac;
using Nethermind.Consensus.Producers;
using Nethermind.Init.Steps;
using Nethermind.Merge.Plugin.InvalidChainTracker;

namespace Nethermind.Taiko;

public class InitializeBlockchainTaiko(TaikoNethermindApi api) : InitializeBlockchain(api)
{
    protected override async Task InitBlockchain()
    {
        await base.InitBlockchain();

        api.Context.Resolve<InvalidChainTracker>().SetupBlockchainProcessorInterceptor(api.MainProcessingContext!.BlockchainProcessor);
    }

    protected override IBlockProductionPolicy CreateBlockProductionPolicy() => NeverStartBlockProductionPolicy.Instance;
}
