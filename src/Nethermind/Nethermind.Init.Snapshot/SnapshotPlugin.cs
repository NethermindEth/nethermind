using Nethermind.Api;
using Nethermind.Api.Extensions;

namespace Nethermind.Init.Snapshot;

public class SnapshotPlugin(ISnapshotConfig snapshotConfig) : IInitializationPlugin
{
    public string Name => "Snapshot";

    public string Author => "Nethermind";

    public string Description => "Plugin providing snapshot functionality";
    public bool Enabled => snapshotConfig is { Enabled: true, DownloadUrl: not null };

    public Task Init(INethermindApi api) => Task.CompletedTask;
    public Task InitNetworkProtocol() => Task.CompletedTask;
    public Task InitRpcModules() => Task.CompletedTask;

    public bool ShouldRunSteps(INethermindApi api) => Enabled;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
