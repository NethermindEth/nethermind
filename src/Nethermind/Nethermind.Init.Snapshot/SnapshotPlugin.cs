using Autofac;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Init.Steps;

namespace Nethermind.Init.Snapshot;

public class SnapshotPlugin(ISnapshotConfig snapshotConfig) : IInitializationPlugin
{
    public string Name => "Snapshot";

    public string Author => "Nethermind";

    public string Description => "Plugin providing snapshot functionality";

    public Task Init(INethermindApi api) => Task.CompletedTask;
    public Task InitNetworkProtocol() => Task.CompletedTask;
    public Task InitRpcModules() => Task.CompletedTask;

    public bool PluginEnabled =>
        snapshotConfig is { Enabled: true, DownloadUrl: not null };

    public void ConfigureContainer(ContainerBuilder builder)
    {
        builder.AddIStepsFromAssembly(GetType().Assembly);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
