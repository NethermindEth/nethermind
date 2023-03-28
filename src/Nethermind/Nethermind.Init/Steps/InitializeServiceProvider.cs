using System;
using System.Threading;
using System.Threading.Tasks;
using MathNet.Numerics;
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
        var originalProvider = _api.ServiceDescriptors.BuildServiceProvider();
        _api.Services = new NethermindApiCustomResolver(originalProvider, _api);
        return Task.CompletedTask;
    }
}
