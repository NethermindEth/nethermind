using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Api.Steps;

namespace Nethermind.Init.Snapshot;

public class SnapshotPlugin(ISnapshotConfig snapshotConfig) : INethermindPlugin
{
    public string Name => "Snapshot";

    public string Author => "Nethermind";

    public string Description => "Plugin providing snapshot functionality";
    public bool Enabled => snapshotConfig is { Enabled: true, DownloadUrl: not null };

    public Task Init(INethermindApi api) => Task.CompletedTask;
    public Task InitNetworkProtocol() => Task.CompletedTask;
    public Task InitRpcModules() => Task.CompletedTask;

    public IEnumerable<StepInfo> GetSteps()
    {
        yield return typeof(InitDatabaseSnapshot);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
