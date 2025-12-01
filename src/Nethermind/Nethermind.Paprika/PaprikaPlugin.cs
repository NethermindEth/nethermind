using Autofac;
using Autofac.Core;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Api.Steps;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Paprika.Importer;
using Nethermind.State;
using Paprika;
using Paprika.Chain;
using Paprika.Merkle;
using Paprika.Store;

namespace Nethermind.Paprika;

public class PaprikaPlugin(IPaprikaConfig paprikaConfig): INethermindPlugin
{
    public string Name => "Paprika";
    public string Description => "Paprika State Db";
    public string Author => "Nethermind";
    public bool Enabled => paprikaConfig.Enabled;
    public IModule Module { get; } = new PaprikaPluginModule(paprikaConfig);
}

public class PaprikaPluginModule(IPaprikaConfig paprikaConfig) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            .AddSingleton<IWorldStateManager, PaprikaWorldStateManager>()
            .AddSingleton<IDb, IInitConfig, IPaprikaConfig>(ConfigureDb)
            .AddSingleton<ComputeMerkleBehavior>((_) => new ComputeMerkleBehavior(ComputeMerkleBehavior.ParallelismNone))
            .AddSingleton<global::Paprika.Chain.Blockchain, IDb, ComputeMerkleBehavior, MetricsReporter>(CreatePaprikaBlockchain)
            .AddSingleton<MetricsReporter>()

            ;

        if (paprikaConfig.ImportFromTrieStore)
        {
            builder.AddStep(typeof(ImporterStep));
        }
    }

    private IDb ConfigureDb(IInitConfig initConfig, IPaprikaConfig paprikaConfig)
    {
        if (initConfig.DiagnosticMode == DiagnosticMode.MemDb)
        {
            return PagedDb.NativeMemoryDb(
                paprikaConfig.SizeGb.GiB(),
                paprikaConfig.HistoryDepth);
        }

        var dbPath = initConfig.BaseDbPath + "/paprika/";

        return PagedDb.MemoryMappedDb(
            paprikaConfig.SizeGb.GiB(),
            paprikaConfig.HistoryDepth, // TODO: CHange history depht to more than a byte
            dbPath,
            paprikaConfig.FlushToDisk);
    }

    private global::Paprika.Chain.Blockchain CreatePaprikaBlockchain(IDb paprikaDb, ComputeMerkleBehavior merkleBehavior, MetricsReporter metricsReporter)
    {
        return new global::Paprika.Chain.Blockchain(
            paprikaDb,
            merkleBehavior,
            minFlushDelay: TimeSpan.FromMinutes(1),
            cacheBudgetStateAndStorage: CacheBudget.Options.None,
            cacheBudgetPreCommit: CacheBudget.Options.None,
            finalizationQueueLimit: paprikaConfig.FinalizationQueueLimit,
            automaticallyFinalizeAfter: paprikaConfig.AutomaticallyFinalizeAfter,
            beforeMetricsDisposed: () => metricsReporter.Observe());
    }
}
