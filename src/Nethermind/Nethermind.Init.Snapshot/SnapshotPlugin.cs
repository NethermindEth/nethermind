using Autofac;
using Autofac.Core;
using Nethermind.Api.Extensions;
using Nethermind.Api.Steps;

namespace Nethermind.Init.Snapshot;

public class SnapshotPlugin(ISnapshotConfig snapshotConfig) : INethermindPlugin
{
    public string Name => "Snapshot";

    public string Author => "Nethermind";

    public string Description => "Plugin providing snapshot functionality";
    public bool Enabled => snapshotConfig is { Enabled: true, DownloadUrl: not null };
    public IModule Module => new SnapshotPluginModule();
}

public class SnapshotPluginModule : Module
{
    protected override void Load(ContainerBuilder builder) => builder.AddStep(typeof(InitDatabaseSnapshot));
}
