using Autofac;
using Autofac.Core;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Config;
using Nethermind.Init.Steps;

namespace Nethermind.Init.Snapshot;

public class SnapshotPlugin : Module, INethermindPlugin
{
    private readonly ISnapshotConfig _config;
    public string Name => "Snapshot";

    public string Author => "Nethermind";

    public string Description => "Plugin providing snapshot functionality";

    public Task Init(INethermindApi api) => Task.CompletedTask;
    public Task InitNetworkProtocol() => Task.CompletedTask;
    public Task InitRpcModules() => Task.CompletedTask;

    public bool Enabled => _config is { Enabled: true, DownloadUrl: not null };

    public IModule? Module => this;

    public SnapshotPlugin(ISnapshotConfig config)
    {
        _config = config;
    }

    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder.RegisterIStepsFromAssembly(GetType().Assembly);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
