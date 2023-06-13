using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;

namespace Nethermind.Optimism;

public class OptimismPlugin : INethermindPlugin
{
    public string Name => "Optimism";

    public string Description => "Optimism support for Nethermind";

    public string Author => "Nethermind";

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public Task Init(INethermindApi nethermindApi)
    {
        return Task.CompletedTask;
    }

    public Task InitNetworkProtocol()
    {
        return Task.CompletedTask;
    }

    public Task InitRpcModules()
    {
        return Task.CompletedTask;
    }
}
