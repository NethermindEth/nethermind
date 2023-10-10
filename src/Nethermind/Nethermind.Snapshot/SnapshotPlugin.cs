using Nethermind.Api;
using Nethermind.Api.Extensions;

namespace Nethermind.Snapshot;

public class SnapshotPlugin : IInitializationPlugin
{
    public string Name => "Snapshot";

    public string Author => "Nethermind";

    public string Description => "Plugin providing snapshot functionality";

    public Task Init(INethermindApi api) => Task.CompletedTask;
    public Task InitNetworkProtocol() => Task.CompletedTask;
    public Task InitRpcModules() => Task.CompletedTask;

    public bool ShouldRunSteps(INethermindApi api) =>
        api.Config<ISnapshotConfig>() is { Enabled: true, DownloadUrl: not null };

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
