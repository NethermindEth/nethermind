using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Api.Factories;
using Nethermind.Consensus.Processing;

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
        services.AddSingleton(_api.ChainSpec ?? throw new StepDependencyException("ChainSpec is null"));
        services.AddSingleton(_api.SpecProvider ?? throw new StepDependencyException("SpecProvider is null"));
        services.AddSingleton(_api.GasLimitCalculator);
        services.AddSingleton<IApiComponentFactory<IBlockProcessor>, BlockProcessorFactory>();
        foreach (INethermindPlugin plugin in _api.Plugins)
        {
            if (plugin is IServiceDescriptorsPlugin serviceDescriptorsPlugin)
                await serviceDescriptorsPlugin.InitServiceDescriptors(services);
        }
    }
}
