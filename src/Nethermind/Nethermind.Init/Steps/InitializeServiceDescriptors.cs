using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Api.Factories;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.State;
using Nethermind.TxPool;
using Nethermind.Wallet;

namespace Nethermind.Init.Steps;

public class InitializeServiceDescriptors : IStep
{
    private readonly INethermindApi _api;

    public InitializeServiceDescriptors(INethermindApi api)
    {
        _api = api;
    }

    public async Task Execute(CancellationToken cancellationToken)
    {
        IServiceCollection services = _api.ServiceDescriptors;
        services.AddSingleton(_api);
        services.AddSingleton(_api.ConfigProvider);
        services.AddSingleton(_api.EthereumJsonSerializer);
        services.AddSingleton(_api.LogManager);
        services.AddSingleton(_api.SealEngineType);
        services.AddSingleton(_api.ChainSpec!);
        services.AddSingleton(_api.SpecProvider!);
        services.AddSingleton(_api.GasLimitCalculator);
        services.AddSingleton<IApiComponentFactory<IBlockProcessor>, BlockProcessorFactory>();

        // Configs
        services.AddSingleton(_api.ConfigProvider.GetConfig<ITxPoolConfig>());
        services.AddSingleton(_api.ConfigProvider.GetConfig<IBlocksConfig>());

        // Blockchain
        services.AddSingleton<ITransactionComparerProvider, TransactionComparerProvider>();
        services.AddSingleton<IReadOnlyStateProvider, ChainHeadReadOnlyStateProvider>();
        services.AddSingleton<ITxValidator, TxValidator>();
        services.AddSingleton<IChainHeadInfoProvider, ChainHeadInfoProvider>();
        services.AddSingleton(provider => _api.TransactionComparerProvider!.GetDefaultComparer());
        services.AddSingleton<ITxPool, TxPool.TxPool>();
        services.AddSingleton<ITxSealer, TxSealer>();
        services.AddSingleton<ITxSigner, WalletTxSigner>();
        services.AddSingleton<INonceManager, NonceManager>();
        services.AddSingleton<ITxPoolInfoProvider, TxPoolInfoProvider>();

        foreach (INethermindPlugin plugin in _api.Plugins)
        {
            if (plugin is IServiceDescriptorsPlugin serviceDescriptorsPlugin)
                await serviceDescriptorsPlugin.InitServiceDescriptors(services);
        }
    }
}
