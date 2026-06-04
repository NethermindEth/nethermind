// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using Autofac;
using Nethermind.Config;
using Nethermind.Consensus.Validators;
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
using Nethermind.Logging;

namespace Nethermind.EraE;

public class EraEModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            .AddSingleton<HttpClient>()
            .AddSingleton<IBeaconRootsProvider>(ctx =>
            {
                string? url = ctx.Resolve<IEraEConfig>().BeaconNodeUrl;
                return string.IsNullOrWhiteSpace(url)
                    ? NullBeaconRootsProvider.Instance
                    : new BeaconApiRootsProvider(new Uri(url), ctx.Resolve<HttpClient>(), logManager: ctx.Resolve<ILogManager>());
            })
            .AddSingleton<IHistoricalSummariesProvider>(ctx =>
            {
                string? url = ctx.Resolve<IEraEConfig>().BeaconNodeUrl;
                return string.IsNullOrWhiteSpace(url)
                    ? NullHistoricalSummariesProvider.Instance
                    : new HistoricalSummariesRpcProvider(new Uri(url), ctx.Resolve<HttpClient>(), logManager: ctx.Resolve<ILogManager>());
            })
            .AddSingleton<IEraImporter, EraImporter>()
            .AddSingleton<IEraExporter, EraExporter>()
            .AddSingleton<IEraStoreFactory>(ctx =>
            {
                IEraEConfig config = ctx.Resolve<IEraEConfig>();
                IRemoteEraClient? remoteClient = string.IsNullOrWhiteSpace(config.RemoteBaseUrl)
                    ? null
                    : new HttpRemoteEraClient(new Uri(config.RemoteBaseUrl), config.RemoteChecksumFile, ctx.Resolve<HttpClient>(), logManager: ctx.Resolve<ILogManager>());

                return new EraStoreFactory(
                    ctx.Resolve<ISpecProvider>(),
                    ctx.Resolve<IBlockValidator>(),
                    ctx.Resolve<IFileSystem>(),
                    config,
                    ctx.Resolve<ILogManager>(),
                    ctx.ResolveOptional<Validator>(),
                    remoteClient);
            })
            .AddSingleton<EraCliRunner>()
            .AddSingleton<IAdminEraService, AdminEraService>()
            .AddSingleton<Validator>(ctx =>
            {
                ISpecProvider spec = ctx.Resolve<ISpecProvider>();
                IHistoricalSummariesProvider summariesProvider = ctx.Resolve<IHistoricalSummariesProvider>();
                IBlocksConfig blocksConfig = ctx.Resolve<IBlocksConfig>();
                return new Validator(spec, trustedAccumulators: null, trustedHistoricalRoots: null, summariesProvider, blocksConfig);
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
