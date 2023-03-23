using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Factories;

namespace Nethermind.Init.Steps;

public class InitializeFactories : IStep
{
    private readonly IApiWithFactories _setApi;

    public InitializeFactories(INethermindApi api)
    {
        (_, _setApi) = api.ForInit;
    }

    public Task Execute(CancellationToken cancellationToken)
    {
        _setApi.BlockProcessorFactory = new BlockProcessorFactory();

        return Task.CompletedTask;
    }
}