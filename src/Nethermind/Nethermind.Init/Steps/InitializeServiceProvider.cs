using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Api;

namespace Nethermind.Init.Steps;

[RunnerStepDependencies(typeof(InitializeServiceDescriptors))]
public class InitializeServiceProvider : IStep
{
    private readonly INethermindApi _api;

    public InitializeServiceProvider(INethermindApi api)
    {
        _api = api;
    }

    public Task Execute(CancellationToken cancellationToken)
    {
        _api.Services = _api.ServiceDescriptors.BuildServiceProvider();
        return Task.CompletedTask;
    }
}
