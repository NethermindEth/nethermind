using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Factories;

namespace Nethermind.Init.Steps;

public class InitializeFactories : IStep
{
    private readonly INethermindApi _api;

    public InitializeFactories(INethermindApi api)
    {
        _api = api;
    }

    public Task Execute(CancellationToken cancellationToken)
    {
        _api.BlockProcessorFactory = new BlockProcessorFactory(_api);

        return Task.CompletedTask;
    }
}