using Autofac;
using Autofac.Core;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Init.Steps;

namespace Nethermind.Init.Snapshot;

public class SnapshotPlugin(ISnapshotConfig snapshotConfig) : INethermindPlugin
{
    public string Name => "Snapshot";

    public string Author => "Nethermind";

    public string Description => "Plugin providing snapshot functionality";

    public Task Init(INethermindApi api) => Task.CompletedTask;
    public Task InitNetworkProtocol() => Task.CompletedTask;
    public Task InitRpcModules() => Task.CompletedTask;

    public bool Enabled =>
        snapshotConfig is { Enabled: true, DownloadUrl: not null };

    public IModule? ContainerModule => new SnapshotPluginModule();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private class SnapshotPluginModule : Module
    {
        protected override void Load(ContainerBuilder builder)
        {
            base.Load(builder);
            builder.AddIStepsFromAssembly(GetType().Assembly);
        }
    }
}
