// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.EraE.Admin;
using Nethermind.EraE.Config;
using Nethermind.EraE.Export;
using Nethermind.EraE.Import;
using Nethermind.EraE.JsonRpc;
using Nethermind.EraE.Proofs;
using Nethermind.EraE.Store;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.EraE;

public class EraEModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            .AddSingleton<IBeaconRootsProvider>(ctx =>
            {
                string? url = ctx.Resolve<IEraEConfig>().BeaconNodeUrl;
                return string.IsNullOrWhiteSpace(url)
                    ? NullBeaconRootsProvider.Instance
                    : new BeaconApiRootsProvider(new Uri(url));
            })
            .AddSingleton<IHistoricalSummariesProvider>(ctx =>
            {
                string? url = ctx.Resolve<IEraEConfig>().BeaconNodeUrl;
                return string.IsNullOrWhiteSpace(url)
                    ? NullHistoricalSummariesProvider.Instance
                    : new HistoricalSummariesRpcProvider(new Uri(url));
            })
            .AddSingleton<IEraImporter, EraImporter>()
            .AddSingleton<IEraExporter, EraExporter>()
            .AddSingleton<IEraStoreFactory, EraStoreFactory>()
            .AddSingleton<EraCliRunner>()
            .AddSingleton<IAdminEraService, AdminEraService>()
            .AddSingleton<Validator>(ctx =>
            {
                ISpecProvider spec = ctx.Resolve<ISpecProvider>();
                IHistoricalSummariesProvider summariesProvider = ctx.Resolve<IHistoricalSummariesProvider>();
                return new Validator(spec, trustedAccumulators: null, trustedHistoricalRoots: null, summariesProvider);
            })
            .RegisterSingletonJsonRpcModule<IEraAdminRpcModule, EraAdminRpcModule>()
            .AddDecorator<IEraEConfig>((ctx, eraConfig) =>
            {
                if (string.IsNullOrWhiteSpace(eraConfig.NetworkName))
                    eraConfig.NetworkName = BlockchainIds.GetBlockchainName(ctx.Resolve<ISpecProvider>().NetworkId);
                return eraConfig;
            });
    }
}
